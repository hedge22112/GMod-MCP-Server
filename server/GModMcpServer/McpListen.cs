using System.Net;

namespace GModMcpServer;

/// <summary>
/// Where <c>--mcp</c> serves Streamable HTTP. Accepts a full URL
/// (<c>http://127.0.0.1:5123</c>, <c>http://0.0.0.0:5123/gmod</c>) or a bare port, which
/// binds loopback only. The endpoint path defaults to <c>/mcp</c>.
/// </summary>
internal sealed record McpListen(string BaseUrl, string Path, string Host)
{
    public const string DefaultPath = "/mcp";

    public string Endpoint => BaseUrl + Path;

    public static McpListen Parse(string spec)
    {
        spec = spec.Trim();

        if (int.TryParse(spec, out var port))
        {
            if (port is < 1 or > 65535)
                throw new ArgumentException($"--mcp port out of range: {spec}");
            return new McpListen($"http://127.0.0.1:{port}", DefaultPath, "127.0.0.1");
        }

        // Kestrel's */+ wildcards aren't valid URI hosts, so they fail here too.
        if (!Uri.TryCreate(spec, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"--mcp expects a port or an http(s) URL such as http://127.0.0.1:5123 (use 0.0.0.0 for all interfaces), got: {spec}");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return new McpListen(uri.GetLeftPart(UriPartial.Authority), path.Length == 0 ? DefaultPath : path, uri.Host);
    }

    /// <summary>
    /// A browser request must come from loopback or the host we were told to bind; anything
    /// else is a foreign page (possibly via DNS rebinding). Non-browser clients send no
    /// Origin and pass.
    /// </summary>
    public bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)) return true;
        if (IPAddress.TryParse(Host, out var bound) && (bound.Equals(IPAddress.Any) || bound.Equals(IPAddress.IPv6Any))) return false;
        return string.Equals(host, Host, StringComparison.OrdinalIgnoreCase);
    }
}
