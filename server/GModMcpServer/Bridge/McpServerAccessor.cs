using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace GModMcpServer.Bridge;

/// <summary>
/// Tracks the live <see cref="McpServer"/> instances so <c>BridgeHostedService</c> can push
/// <c>notifications/tools/list_changed</c> when the GMod manifest changes. The SDK doesn't
/// expose its servers via DI, but every <see cref="RequestContext{T}"/> carries one, so the
/// tool handlers in <c>Program.cs</c> add it on each call. Over stdio that is the single
/// server; over HTTP (<c>--mcp</c>) every session has its own, added and removed by the
/// session handler for its lifetime.
/// </summary>
public sealed class McpServerAccessor
{
    private readonly ConcurrentDictionary<McpServer, byte> _servers = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyCollection<McpServer> Servers => _servers.Keys.ToArray();

    public void Add(McpServer? server)
    {
        if (server is null) return;
        _servers.TryAdd(server, 0);
    }

    public void Remove(McpServer server) => _servers.TryRemove(server, out _);
}
