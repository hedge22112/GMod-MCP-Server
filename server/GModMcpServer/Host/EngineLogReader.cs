using System.Text;
using GModMcpServer.Remote;

namespace GModMcpServer.Host;

/// <summary>A byte range of the log and the file's total length when it was read.</summary>
public sealed record LogChunk(long Length, long Start, byte[] Data);

/// <summary>
/// Random access to the log file, so the same tailing logic serves a local install and a
/// remote one over ssh. Reads return the file length alongside the bytes because each
/// remote call is a round trip.
/// </summary>
public interface ILogFile
{
    string DisplayPath { get; }

    /// <summary>Length and last write time, or null when the file doesn't exist.</summary>
    (long Length, DateTime LastWriteUtc)? Stat();

    /// <summary>
    /// Up to <paramref name="max"/> bytes from <paramref name="offset"/>, or from
    /// <c>Length + offset</c> (clamped to 0) when it's negative. Null when the file doesn't exist.
    /// </summary>
    LogChunk? Read(long offset, int max);
}

public sealed class LocalLogFile : ILogFile
{
    private readonly string _path;

    public LocalLogFile(string path) => _path = path;

    public string DisplayPath => _path;

    public (long Length, DateTime LastWriteUtc)? Stat()
    {
        var info = new FileInfo(_path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    public LogChunk? Read(long offset, int max)
    {
        if (!File.Exists(_path)) return null;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = fs.Length;
        var start = offset < 0 ? Math.Max(0, len + offset) : offset;
        if (start >= len || max <= 0) return new LogChunk(len, start, Array.Empty<byte>());

        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[(int)Math.Min(len - start, max)];
        var n = ReadFull(fs, buf, buf.Length);
        return new LogChunk(len, start, n == buf.Length ? buf : buf[..n]);
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        var off = 0;
        while (off < count)
        {
            var got = s.Read(buf, off, count - off);
            if (got <= 0) break; // EOF (the file may have been appended-to concurrently; snapshot is fine)
            off += got;
        }
        return off;
    }
}

/// <summary>
/// The log on a remote server. The reader's API is synchronous (it runs under
/// <see cref="EngineLog"/>'s lock), so this blocks on the round trip. A dropped session
/// throws rather than reading as a missing file, which would reset the caller's cursor
/// and replay the whole log once it reconnects.
/// </summary>
public sealed class RemoteLogFile : ILogFile
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly SshAgent _agent;
    private readonly string _rel;

    public RemoteLogFile(SshAgent agent, string rel)
    {
        _agent = agent;
        _rel = rel;
    }

    public string DisplayPath => _agent.Describe(_rel);

    public (long Length, DateTime LastWriteUtc)? Stat()
    {
        var st = Run(ct => _agent.StatAsync(_rel, ct));
        return st is null ? null : (st.Length, st.LastWriteUtc);
    }

    public LogChunk? Read(long offset, int max)
    {
        var chunk = Run(ct => _agent.ReadAsync(_rel, offset, max, ct));
        return chunk is null ? null : new LogChunk(chunk.Length, chunk.Start, chunk.Data);
    }

    private static T? Run<T>(Func<CancellationToken, Task<T?>> op) where T : class
    {
        using var cts = new CancellationTokenSource(Timeout);
        return op(cts.Token).GetAwaiter().GetResult();
    }
}

/// <summary>
/// Tails GMod's engine console log (<c>garrysmod/console.log</c>, produced by
/// <c>-condebug</c>). Pure file I/O with no persistent state — the caller owns the
/// byte cursor — so it's unit-testable against a temp file.
///
/// Local reads use <see cref="FileShare.ReadWrite"/> because the engine holds the file
/// open for writing the whole time (verified: shared reads succeed live). Decoding
/// is Latin1 so every byte maps to exactly one char — byte offsets equal char
/// indices, which keeps the cursor math exact — and no byte sequence can throw.
///
/// <c>-condebug</c> APPENDS across launches (never truncates), so the file
/// accumulates every session; callers anchor their cursor per-launch rather than
/// reading from 0. A cursor past end-of-file (the file was deleted/rotated) resets
/// to 0. A trailing partial line (caught mid-write) is held back until its newline
/// arrives.
/// </summary>
public sealed class EngineLogReader
{
    // Cap a single incremental read so a first read against a multi-MB backlog is
    // bounded; the next call continues from the advanced cursor.
    private const int MaxChunkBytes = 1 << 20; // 1 MiB
    // How far back a no-cursor "tail" read looks.
    private const int TailWindowBytes = 256 * 1024;

    private readonly ILogFile _file;

    public EngineLogReader(string path) : this(new LocalLogFile(path)) { }

    public EngineLogReader(ILogFile file) => _file = file;

    public bool Exists => _file.Stat() is not null;

    public long Length => _file.Stat()?.Length ?? 0;

    /// <summary>
    /// Return complete lines from <paramref name="cursor"/> up to the last newline,
    /// advancing the cursor past them. Empty when there's nothing new or only a
    /// partial line. Resets the cursor to 0 if it points past end-of-file.
    /// </summary>
    public IReadOnlyList<string> ReadFrom(ref long cursor)
    {
        var chunk = _file.Read(cursor, MaxChunkBytes);
        if (chunk is null) { cursor = 0; return Array.Empty<string>(); }
        if (cursor > chunk.Length)               // truncated / rotated -> restart
        {
            cursor = 0;
            chunk = _file.Read(0, MaxChunkBytes);
            if (chunk is null) return Array.Empty<string>();
        }
        if (chunk.Data.Length == 0) return Array.Empty<string>();

        var n = chunk.Data.Length;
        var text = Encoding.Latin1.GetString(chunk.Data);

        var lastNl = text.LastIndexOf('\n');
        if (lastNl < 0)
        {
            // No complete line. Advance past a capped chunk (a pathological unbroken
            // run) so we don't stall; otherwise hold and wait for the newline.
            if (n >= MaxChunkBytes) cursor += n;
            return Array.Empty<string>();
        }

        var complete = text.Substring(0, lastNl); // excludes the final '\n'
        cursor += lastNl + 1;                      // Latin1: char count == byte count
        return SplitLines(complete);
    }

    /// <summary>
    /// Return the last <paramref name="maxLines"/> complete lines and the
    /// end-of-file cursor (so a follow-up read with that cursor continues from here).
    /// Reads only a bounded window off the end of the file.
    /// </summary>
    public (IReadOnlyList<string> Lines, long Cursor) ReadTail(int maxLines)
    {
        var chunk = _file.Read(-TailWindowBytes, TailWindowBytes);
        if (chunk is null) return (Array.Empty<string>(), 0);

        var text = Encoding.Latin1.GetString(chunk.Data);

        if (chunk.Start > 0)
        {
            // Started mid-line; drop the partial first line.
            var firstNl = text.IndexOf('\n');
            text = firstNl >= 0 ? text.Substring(firstNl + 1) : "";
        }

        var lines = SplitLines(text.TrimEnd('\n'));
        if (lines.Count > maxLines)
        {
            lines = lines.Skip(lines.Count - maxLines).ToList();
        }
        return (lines, chunk.Length);
    }

    private static List<string> SplitLines(string block)
    {
        if (block.Length == 0) return new List<string>();
        var raw = block.Split('\n');
        var lines = new List<string>(raw.Length);
        foreach (var l in raw) lines.Add(l.TrimEnd('\r'));
        return lines;
    }
}
