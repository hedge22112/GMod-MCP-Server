using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GModMcpServer.Remote;

/// <summary>
/// Where a remote GMod server lives: an ssh destination (anything <c>ssh</c> accepts,
/// including a <c>~/.ssh/config</c> alias) and optionally its <c>garrysmod/data</c>
/// path. Parsed from <c>--ssh [user@]host[:/path/to/garrysmod/data]</c>.
/// </summary>
public sealed record SshTarget(string Destination, string? DataPath)
{
    public static SshTarget Parse(string spec, string? dataPathOverride)
    {
        var s = spec.Trim();
        string? path = null;
        var colon = s.IndexOf(':');
        if (colon >= 0)
        {
            path = s[(colon + 1)..];
            s = s[..colon];
        }
        if (string.IsNullOrWhiteSpace(s))
        {
            throw new ArgumentException($"--ssh needs a host, got '{spec}'.");
        }
        if (!string.IsNullOrEmpty(dataPathOverride)) path = dataPathOverride;
        return new SshTarget(s, string.IsNullOrWhiteSpace(path) ? null : path);
    }
}

public sealed record RemoteChunk(long Length, long Start, byte[] Data);

public sealed record RemoteStat(long Length, DateTime LastWriteUtc);

/// <summary>
/// One long-lived <c>ssh</c> session running <c>agent.sh</c> on the remote host, giving
/// the bridge file access to a GMod server's <c>garrysmod/data</c> without anything
/// installed there. The system <c>ssh</c> is used rather than a .NET SSH library so
/// the user's own config, keys and agent apply unchanged; <c>BatchMode</c> means it
/// fails rather than prompting, since there is no terminal to prompt on.
///
/// Requests are tagged and answered in any order. Responses to bridge calls are not
/// polled for: a one-shot <c>watch</c> makes the agent push the file as soon as it
/// appears, so each call costs one round trip plus the game's own poll interval.
/// A dropped session reconnects with backoff and re-registers watches and tracks.
/// </summary>
public sealed class SshAgent : IDisposable
{
    private const string Prefix = "@@M ";
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    };

    private readonly SshTarget _target;
    private readonly string _sshExe;
    private readonly Action<ProcessStartInfo, string> _command;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string[]>> _ops = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _watches = new();
    private readonly ConcurrentDictionary<string, Action<string?>> _tracks = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _proc;
    private long _nextTag;
    private Task? _supervisor;

    public SshAgent(SshTarget target, string sshExe, ILogger log)
        : this(target, string.IsNullOrWhiteSpace(sshExe) ? "ssh" : sshExe, (psi, remote) =>
        {
            foreach (var a in new[]
            {
                "-T",
                "-o", "BatchMode=yes",
                "-o", "ServerAliveInterval=15",
                "-o", "ServerAliveCountMax=3",
                target.Destination,
                remote,
            })
            {
                psi.ArgumentList.Add(a);
            }
        }, log)
    {
    }

    /// <summary>
    /// Runs <paramref name="exe"/> set up by <paramref name="command"/> to run the remote
    /// shell command, so tests can stand a local shell in for ssh.
    /// </summary>
    internal SshAgent(SshTarget target, string exe, Action<ProcessStartInfo, string> command, ILogger log)
    {
        _target = target;
        _sshExe = exe;
        _command = command;
        _log = log;
    }

    public string Destination => _target.Destination;

    /// <summary>The remote <c>garrysmod/data</c>, resolved by the agent. Null until first connect.</summary>
    public string? DataPath { get; private set; }

    /// <summary>The remote kernel (<c>uname -sr</c>), for status output.</summary>
    public string? RemoteSystem { get; private set; }

    public bool Connected { get; private set; }

    /// <summary>Why the session is down, when it is: the agent's fatal message or ssh's last stderr.</summary>
    public string? LastError { get; private set; }

    /// <summary>A path inside the remote data dir as <c>host:/abs/path</c>, for display.</summary>
    public string Describe(string rel)
    {
        var root = DataPath ?? _target.DataPath ?? "<garrysmod/data>";
        var full = rel.StartsWith("../", StringComparison.Ordinal)
            ? ParentOf(root) + "/" + rel[3..]
            : root.TrimEnd('/') + "/" + rel;
        return _target.Destination + ":" + full;
    }

    public void Start()
    {
        _supervisor ??= Task.Run(() => SuperviseAsync(_shutdown.Token));
    }

    /// <summary>
    /// Wait until the first connection attempt has either come up or failed, up to
    /// <paramref name="timeout"/>, and report whether the session is up. Later attempts
    /// carry on in the background.
    /// </summary>
    public async Task<bool> WaitFirstAttemptAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _firstAttempt.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* still connecting */ }
        return Connected;
    }

    public async Task PutAsync(string rel, string content, CancellationToken ct)
    {
        var reply = await RequestAsync("put", ct, rel, Convert.ToBase64String(Encoding.UTF8.GetBytes(content))).ConfigureAwait(false);
        if (reply[0] == "err")
        {
            throw new IOException($"{Destination}: {DecodeText(Field(reply, 2))}");
        }
    }

    public Task RemoveAsync(string rel, CancellationToken ct) => RequestAsync("rm", ct, rel);

    /// <summary>Best-effort delete that nobody waits on.</summary>
    public void RemoveNoWait(string rel)
    {
        _ = Task.Run(async () =>
        {
            try { await RemoveAsync(rel, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
        });
    }

    /// <summary>
    /// Up to <paramref name="max"/> bytes from <paramref name="offset"/>, or from the end
    /// when <paramref name="offset"/> is negative. Null when the file doesn't exist.
    /// </summary>
    public async Task<RemoteChunk?> ReadAsync(string rel, long offset, int max, CancellationToken ct)
    {
        var reply = await RequestAsync("read", ct, rel, offset.ToString(), max.ToString()).ConfigureAwait(false);
        var length = long.Parse(Field(reply, 2));
        if (length < 0) return null;
        var start = long.Parse(Field(reply, 3));
        var data = Field(reply, 4);
        return new RemoteChunk(length, start, data == "-" ? Array.Empty<byte>() : Convert.FromBase64String(data));
    }

    public async Task<RemoteStat?> StatAsync(string rel, CancellationToken ct)
    {
        var reply = await RequestAsync("stat", ct, rel).ConfigureAwait(false);
        if (Field(reply, 2) == "-") return null;
        return new RemoteStat(
            long.Parse(Field(reply, 2)),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(Field(reply, 3))).UtcDateTime);
    }

    /// <summary>srcds processes running from this install, as raw agent lines.</summary>
    public async Task<IReadOnlyList<RemoteProcess>> ListProcessesAsync(CancellationToken ct)
    {
        var reply = await RequestAsync("proc", ct).ConfigureAwait(false);
        var list = new List<RemoteProcess>();
        if (Field(reply, 2) == "-") return list;
        foreach (var line in DecodeText(Field(reply, 2)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !int.TryParse(parts[0], out var pid)) continue;
            long? elapsed = long.TryParse(parts[1], out var e) ? e : null;
            list.Add(new RemoteProcess(pid, elapsed, parts[2].Trim()));
        }
        return list;
    }

    /// <summary>
    /// Complete with the file's contents once it exists and is non-empty. A single-use
    /// watch: call again to wait for a rewrite (e.g. after reading a partial file).
    /// </summary>
    public async Task<string> WatchAsync(string rel, CancellationToken ct)
    {
        ValidateRel(rel);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watches[rel] = tcs;
        try
        {
            await SendLineAsync($"watch {rel}", ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (_watches.TryRemove(new KeyValuePair<string, TaskCompletionSource<string>>(rel, tcs)) && !tcs.Task.IsCompleted)
            {
                _ = SendLineAsync($"unwatch {rel}", CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Call <paramref name="onChange"/> with the file's contents whenever they change, and
    /// with null when it's absent. Fires once on registration and after every reconnect.
    /// </summary>
    public void Track(string rel, Action<string?> onChange)
    {
        ValidateRel(rel);
        _tracks[rel] = onChange;
        _ = SendLineAsync($"track {rel}", CancellationToken.None);
    }

    private async Task<string[]> RequestAsync(string cmd, CancellationToken ct, params string[] args)
    {
        if (args.Length > 0 && cmd != "proc") ValidateRel(args[0]);
        if (!Connected)
        {
            throw new IOException($"not connected to {Destination} over ssh" + (LastError is null ? "" : $": {LastError}"));
        }

        var tag = Interlocked.Increment(ref _nextTag).ToString();
        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ops[tag] = tcs;
        try
        {
            var line = args.Length == 0 ? $"{cmd} {tag}" : $"{cmd} {tag} {string.Join(' ', args)}";
            await SendLineAsync(line, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(OpTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException($"{Destination} did not answer '{cmd}' within {OpTimeout.TotalSeconds:F0}s");
        }
        finally
        {
            _ops.TryRemove(tag, out _);
        }
    }

    private async Task SendLineAsync(string line, CancellationToken ct)
    {
        var proc = _proc;
        if (proc is null || !Connected) return; // watches/tracks are re-sent on reconnect
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await proc.StandardInput.WriteAsync(line + "\n").ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The session died under us; the reader notices and fails the pending ops.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var wasUp = await RunSessionAsync(ct).ConfigureAwait(false);
            _firstAttempt.TrySetResult();
            if (ct.IsCancellationRequested) break;
            if (wasUp) attempt = 0;
            var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            _log.LogWarning("ssh session to {Host} ended ({Error}); reconnecting in {Delay}s",
                Destination, LastError ?? "no error reported", delay.TotalSeconds);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    // Runs one ssh session to completion. Returns whether it got as far as the agent's hello.
    private async Task<bool> RunSessionAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_sshExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _command(psi, RemoteCommand());

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new IOException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            LastError = $"could not start '{_sshExe}': {ex.Message}";
            return false;
        }

        _proc = proc;
        lock (_stderrTail) _stderrTail.Clear();
        _ = Task.Run(() => PumpStderrAsync(proc));

        var sawHello = false;
        using var killOnShutdown = ct.Register(() => TryKill(proc));
        try
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue; // login-script chatter
                var fields = line[Prefix.Length..].Split(' ');
                if (fields[0] == "hello")
                {
                    sawHello = true;
                    OnHello(fields);
                    continue;
                }
                Dispatch(fields);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ssh session to {Host} read failed", Destination);
        }

        try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { TryKill(proc); }

        OnDisconnected(proc);
        return sawHello;
    }

    private void OnHello(string[] fields)
    {
        DataPath = DecodeText(Field(fields, 1));
        RemoteSystem = DecodeText(Field(fields, 2));
        LastError = null;
        Connected = true;
        _log.LogInformation("Connected to {Host}: {DataPath} ({System})", Destination, DataPath, RemoteSystem);

        // Re-arm what outlived a previous session. Tracks re-fire their current content,
        // which the consumers treat as idempotent.
        foreach (var rel in _watches.Keys) _ = SendLineAsync($"watch {rel}", CancellationToken.None);
        foreach (var rel in _tracks.Keys) _ = SendLineAsync($"track {rel}", CancellationToken.None);

        _firstAttempt.TrySetResult();
    }

    private void OnDisconnected(Process proc)
    {
        Connected = false;
        _proc = null;

        string tail;
        lock (_stderrTail) tail = string.Join(" | ", _stderrTail);
        if (LastError is null || !string.IsNullOrEmpty(tail))
        {
            var exit = proc.HasExited ? $"ssh exited with code {proc.ExitCode}" : "ssh session closed";
            LastError = string.IsNullOrEmpty(tail) ? LastError ?? exit : tail;
        }
        proc.Dispose();

        var error = new IOException($"ssh connection to {Destination} lost: {LastError}");
        foreach (var kv in _ops)
        {
            if (_ops.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(error);
        }
    }

    private void Dispatch(string[] f)
    {
        switch (f[0])
        {
            case "ok":
            case "err":
            case "data":
            case "stat":
            case "proc":
                if (_ops.TryRemove(Field(f, 1), out var op)) op.TrySetResult(f);
                break;
            case "file":
                if (_watches.TryRemove(Field(f, 1), out var watch)) watch.TrySetResult(DecodeText(Field(f, 2)));
                break;
            case "track":
                if (_tracks.TryGetValue(Field(f, 1), out var onChange))
                {
                    var body = Field(f, 2);
                    try { onChange(body == "-" ? null : DecodeText(body)); }
                    catch (Exception ex) { _log.LogWarning(ex, "Track handler for {Rel} failed", f[1]); }
                }
                break;
            case "fatal":
                LastError = DecodeText(Field(f, 1));
                _log.LogError("Remote agent on {Host} stopped: {Error}", Destination, LastError);
                break;
        }
    }

    private async Task PumpStderrAsync(Process proc)
    {
        try
        {
            while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _log.LogDebug("ssh stderr: {Line}", line);
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line.Trim());
                    while (_stderrTail.Count > 3) _stderrTail.Dequeue();
                }
            }
        }
        catch { /* process gone */ }
    }

    // The remote login shell parses this, so everything variable travels as base64,
    // which needs no quoting. `bash -c` gets the script as its argument, keeping stdin
    // free for commands.
    private string RemoteCommand()
    {
        var script = LoadScript().Replace("\r", "");
        var scriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var pathB64 = _target.DataPath is null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(_target.DataPath));
        return $"exec bash -c \"$(echo {scriptB64} | base64 -d)\" gmodmcp-agent {pathB64}";
    }

    internal static string LoadScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("GModMcpServer.Remote.agent.sh")
            ?? throw new InvalidOperationException("agent.sh is not embedded in the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Relative paths go on the wire unquoted, so they must be a single safe word.
    private static void ValidateRel(string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException($"invalid remote path '{rel}'");
        }
    }

    private static string Field(string[] f, int i) => i < f.Length ? f[i] : "";

    private static string DecodeText(string b64) =>
        string.IsNullOrEmpty(b64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash > 0 ? trimmed[..slash] : trimmed;
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        var proc = _proc;
        if (proc is not null) TryKill(proc);
        try { _supervisor?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _shutdown.Dispose();
    }
}

public sealed record RemoteProcess(int Pid, long? ElapsedSeconds, string CommandLine);
