using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace GModMcpServer.Host;

/// <summary>
/// Source RCON over TCP. Reaches the server's console without the bridge, so it still works
/// when the bridge is down (mcp_enable 0, Lua errors at boot), and it can send <c>quit</c>,
/// which Lua can't. Connects per command: srcds drops idle connections, and commands are rare.
/// </summary>
public sealed class RconClient
{
    private const int AuthRequest = 3;
    private const int AuthResponse = 2;
    private const int ExecCommand = 2;
    private const int ResponseValue = 0;
    private const int MaxPacket = 4096 + 10;

    private readonly string _password;

    public RconClient(string host, int port, string password)
    {
        Host = host;
        Port = port;
        _password = password;
    }

    public string Host { get; }

    public int Port { get; }

    public string Endpoint => $"{Host}:{Port}";

    /// <summary>
    /// Parse <c>host[:port]</c>; the port defaults to 27015, srcds's default.
    /// </summary>
    public static RconClient Parse(string spec, string password)
    {
        spec = spec.Trim();
        var host = spec;
        var port = 27015;
        var colon = spec.LastIndexOf(':');
        if (colon > 0 && !spec.EndsWith(']'))
        {
            if (!int.TryParse(spec[(colon + 1)..], out port) || port is < 1 or > 65535)
                throw new ArgumentException($"--rcon port is not valid: {spec}");
            host = spec[..colon];
        }
        host = host.Trim('[', ']');
        if (host.Length == 0) throw new ArgumentException($"--rcon needs a host: {spec}");
        return new RconClient(host, port, password);
    }

    /// <summary>
    /// Run one console command and return its output. <paramref name="expectDisconnect"/> is for
    /// <c>quit</c>: the server closing the connection then counts as success.
    /// </summary>
    public async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct, bool expectDisconnect = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(Host, Port, cts.Token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new IOException($"could not connect to RCON at {Endpoint}: {ex.Message}", ex);
        }
        var stream = tcp.GetStream();

        await WriteAsync(stream, 1, AuthRequest, _password, cts.Token).ConfigureAwait(false);
        // srcds sends an empty RESPONSE_VALUE before the AUTH_RESPONSE; its id is -1 on a bad password.
        while (true)
        {
            var (id, type, _) = await ReadAsync(stream, cts.Token).ConfigureAwait(false);
            if (type != AuthResponse) continue;
            if (id == -1) throw new UnauthorizedAccessException($"RCON at {Endpoint} rejected the password");
            break;
        }

        await WriteAsync(stream, 2, ExecCommand, command, cts.Token).ConfigureAwait(false);
        // Output can span several packets. An empty RESPONSE_VALUE sent after the command is
        // mirrored back once the command's output is done, which marks the end.
        await WriteAsync(stream, 3, ResponseValue, "", cts.Token).ConfigureAwait(false);

        var output = new StringBuilder();
        try
        {
            while (true)
            {
                var (id, _, body) = await ReadAsync(stream, cts.Token).ConfigureAwait(false);
                if (id == 3) break;
                if (id == 2) output.Append(body);
            }
        }
        catch (EndOfStreamException) when (expectDisconnect)
        {
            // quit closes the socket before the end marker.
        }
        catch (IOException) when (expectDisconnect)
        {
        }
        return output.ToString();
    }

    private static async Task WriteAsync(NetworkStream stream, int id, int type, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + bytes.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        bytes.CopyTo(packet, 12);
        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
    }

    private static async Task<(int Id, int Type, string Body)> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 10 || size > MaxPacket) throw new IOException($"RCON sent a malformed packet (size {size})");

        var rest = new byte[size];
        await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
        var id = BinaryPrimitives.ReadInt32LittleEndian(rest);
        var type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4));
        var body = Encoding.UTF8.GetString(rest, 8, size - 10);
        return (id, type, body);
    }
}
