using System.Text.Json;
using GModMcpServer.Models;

namespace GModMcpServer.Bridge;

/// <summary>
/// One realm's request/response channel to GMod: <see cref="FileBridge"/> for a game on
/// this machine, <see cref="Remote.SshBridge"/> for one reached over ssh.
/// </summary>
public interface IBridge : IDisposable
{
    string Realm { get; }

    Task<BridgeResponse> SendAsync(string functionId, JsonElement args, TimeSpan timeout, CancellationToken ct);
}
