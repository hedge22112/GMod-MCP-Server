using System.Text.Json;
using System.Text.Json.Nodes;
using GModMcpServer.Bridge;
using GModMcpServer.Models;

namespace GModMcpServer.Remote;

/// <summary>
/// The remote counterpart of <see cref="FileBridge"/>: the same request/response files,
/// written and read on the far side of an <see cref="SshAgent"/>. Instead of polling the
/// out dir it registers a one-shot watch for its own response file before writing the
/// request, so the agent pushes the reply the moment GMod writes it.
/// </summary>
public sealed class SshBridge : IBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly SshAgent _agent;
    private readonly string _sessionPrefix;

    public SshBridge(SshAgent agent, string realm, string sessionId)
    {
        _agent = agent;
        Realm = realm;
        _sessionPrefix = sessionId + "__";
    }

    public string Realm { get; }

    public async Task<BridgeResponse> SendAsync(string functionId, JsonElement args, TimeSpan timeout, CancellationToken ct)
    {
        var id = _sessionPrefix + Guid.NewGuid().ToString("N");
        var inRel = $"mcp/{Realm}/in/{id}.json";
        var outRel = $"mcp/{Realm}/out/{id}.json";

        var req = new BridgeRequest
        {
            Id = id,
            FunctionId = functionId,
            Args = JsonNode.Parse(args.GetRawText()),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var answered = false;
        try
        {
            // Watch first so a fast reply can't land before anyone is listening for it.
            var reply = _agent.WatchAsync(outRel, cts.Token);
            await _agent.PutAsync(inRel, JsonSerializer.Serialize(req, JsonOpts), cts.Token).ConfigureAwait(false);

            while (true)
            {
                var raw = await reply.ConfigureAwait(false);
                BridgeResponse? resp = null;
                try { resp = JsonSerializer.Deserialize<BridgeResponse>(raw, JsonOpts); }
                catch (JsonException) { /* caught mid-write; wait for the rest */ }

                if (resp is not null)
                {
                    answered = true;
                    _agent.RemoveNoWait(outRel);
                    return resp;
                }
                await Task.Delay(30, cts.Token).ConfigureAwait(false);
                reply = _agent.WatchAsync(outRel, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Callers treat TaskCanceledException as "GMod didn't answer in time".
            throw new TaskCanceledException();
        }
        finally
        {
            if (!answered)
            {
                // Release the watch if the put failed, and, as the local bridge does,
                // don't leave an unprocessed request to fire later.
                cts.Cancel();
                if (_agent.Connected) _agent.RemoveNoWait(inRel);
            }
        }
    }

    public void Dispose() { }
}
