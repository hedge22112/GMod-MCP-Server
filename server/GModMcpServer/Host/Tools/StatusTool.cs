using System.Text.Json;
using System.Text.Json.Nodes;
using GModMcpServer.Bridge;
using GModMcpServer.Remote;
using ModelContextProtocol.Protocol;

namespace GModMcpServer.Host.Tools;

public sealed class StatusTool : IHostTool
{
    private readonly GameProcessManager _proc;
    private readonly ManifestWatcher _manifest;
    private readonly BridgePinger _pinger;
    private readonly EngineLog _engineLog;
    private readonly BridgePaths _paths;

    public StatusTool(GameProcessManager proc, ManifestWatcher manifest, BridgePinger pinger, EngineLog engineLog, BridgePaths paths)
    {
        _proc = proc;
        _manifest = manifest;
        _pinger = pinger;
        _engineLog = engineLog;
        _paths = paths;
    }

    public string Name => "host_status";

    public string Description =>
        "Report whether GMod is running, whether the MCP bridge is reachable (a live ping is " +
        "sent when GMod is detected), and the current tool count and capability state. " +
        "Useful for diagnosing why a tool call isn't working.";

    public JsonElement InputSchema { get; } = HostToolHelpers.ParseSchema("""
    { "type": "object", "properties": {}, "required": [] }
    """);

    public async ValueTask<CallToolResult> InvokeAsync(IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        if (_paths.Remote is { } remote)
        {
            return await RemoteStatusAsync(remote, ct).ConfigureAwait(false);
        }

        var snap = _proc.Snapshot();
        var manifest = _manifest.Current;

        var bridgeNode = new JsonObject
        {
            ["tools"] = manifest.Tools.Count,
        };

        BridgePingResult? ping = null;
        if (snap.Running)
        {
            var p = await _pinger.PingAsync(ct).ConfigureAwait(false);
            ping = p;
            FillPing(bridgeNode, p, "GMod is running but the bridge didn't respond — likely still loading, or paused on a menu.");
        }
        else
        {
            bridgeNode["reachable"] = false;
            bridgeNode["latency_ms"] = null;
            bridgeNode["enabled"] = null;
            bridgeNode["map"] = null;
            bridgeNode["maxplayers"] = null;
            bridgeNode["singleplayer"] = null;
            bridgeNode["bootstrap_pending"] = null;
        }

        bridgeNode["capabilities"] = Capabilities(manifest, ping);

        // Engine-log capture. `condebug` is the definitive signal: it reads the running
        // process's real command line (via WMI), so it's right even for a Steam-started
        // game we didn't launch. `present`/`recently_written` are only a proxy — console.log
        // persists across sessions (-condebug appends), so a stale file can be "present"
        // while this session has no -condebug. Surfaced prominently so the agent knows
        // up-front whether engine output is being captured, rather than silently missing it.
        var realCmdline = snap.Running ? _proc.GetRunningCommandLine() : null;
        var condebug = snap.Running ? GameProcessManager.HasCondebug(realCmdline) : (bool?)null;
        var lastWrite = _engineLog.LastWriteUtc;
        var recentlyWritten = lastWrite is { } t && (DateTime.UtcNow - t).TotalSeconds < 30;

        string engineNote;
        if (!snap.Running)
            engineNote = "GMod isn't running.";
        else if (condebug == true)
            engineNote = "-condebug is on: engine output is captured to console.log — read it with engine_log; serious warnings also ride engine_events.";
        else if (condebug == false)
            engineNote = _engineLog.Present
                ? "-condebug is NOT on the running process: engine output is NOT captured this session (any console.log is a stale prior-session file). console_read / engine_events silently miss engine-native messages. Relaunch via host_launch (adds -condebug) or add it to Steam launch options."
                : "-condebug is NOT on and there's no console.log: engine output isn't captured. Relaunch via host_launch (adds -condebug) or add it to Steam launch options.";
        else
            engineNote = _engineLog.Present
                ? "Couldn't read the launch args to confirm -condebug; console.log is present" + (recentlyWritten ? " and recently written, so capture is likely active." : ", but not recently written, so capture may be stale/off.")
                : "Couldn't read the launch args and there's no console.log — engine output likely isn't captured.";

        var engineNode = new JsonObject
        {
            ["condebug"] = condebug,
            ["capturing"] = snap.Running ? (condebug ?? recentlyWritten) : (bool?)null,
            ["present"] = _engineLog.Present,
            ["path"] = _engineLog.Path,
            ["recently_written"] = recentlyWritten,
            ["command_line"] = realCmdline,
            ["note"] = engineNote,
        };

        var result = new JsonObject
        {
            ["ok"] = true,
            ["gmod"] = new JsonObject
            {
                ["running"] = snap.Running,
                ["pid"] = snap.Pid,
                ["uptime_seconds"] = snap.Uptime?.TotalSeconds,
                ["last_launch_args"] = string.IsNullOrEmpty(snap.LastArgs) ? null : snap.LastArgs,
            },
            ["bridge"] = bridgeNode,
            ["engine_log"] = engineNode,
        };

        return HostToolHelpers.Ok(result.ToJsonString());
    }

    // A remote server isn't a local process: whether it's up comes from the srcds
    // processes the agent finds in that install, and the ping is always sent.
    private async ValueTask<CallToolResult> RemoteStatusAsync(SshAgent remote, CancellationToken ct)
    {
        var manifest = _manifest.Current;
        var remoteNode = new JsonObject
        {
            ["ssh"] = remote.Destination,
            ["connected"] = remote.Connected,
            ["data_path"] = remote.DataPath,
            ["system"] = remote.RemoteSystem,
        };

        if (!remote.Connected)
        {
            remoteNode["error"] = remote.LastError;
            remoteNode["hint"] = "The ssh session isn't up; it retries in the background. Check that `ssh "
                + remote.Destination + "` works non-interactively (key or agent auth, host key already accepted).";
            return HostToolHelpers.Ok(new JsonObject
            {
                ["ok"] = true,
                ["remote"] = remoteNode,
                ["bridge"] = new JsonObject { ["tools"] = manifest.Tools.Count, ["reachable"] = false },
            }.ToJsonString());
        }

        IReadOnlyList<RemoteProcess> procs;
        try { procs = await remote.ListProcessesAsync(ct).ConfigureAwait(false); }
        catch { procs = Array.Empty<RemoteProcess>(); }

        var gmodNode = new JsonObject { ["running"] = procs.Count > 0 };
        if (procs.Count > 0)
        {
            gmodNode["pid"] = procs[0].Pid;
            gmodNode["uptime_seconds"] = procs[0].ElapsedSeconds;
            gmodNode["command_line"] = procs[0].CommandLine;
        }

        var p = await _pinger.PingAsync(ct).ConfigureAwait(false);
        var bridgeNode = new JsonObject { ["tools"] = manifest.Tools.Count };
        FillPing(bridgeNode, p, procs.Count > 0
            ? "The server is running but the bridge didn't respond — likely still loading, or the addon isn't installed there."
            : "No srcds process found in this install and the bridge didn't respond — is the server running?");
        bridgeNode["dedicated"] = p.Dedicated;
        bridgeNode["capabilities"] = Capabilities(manifest, p);

        bool? condebug = procs.Count > 0 ? GameProcessManager.HasCondebug(procs[0].CommandLine) : null;
        var lastWrite = _engineLog.LastWriteUtc;
        var recentlyWritten = lastWrite is { } t && (DateTime.UtcNow - t).TotalSeconds < 30;
        var engineNode = new JsonObject
        {
            ["condebug"] = condebug,
            ["present"] = lastWrite is not null,
            ["path"] = _engineLog.Path,
            ["recently_written"] = recentlyWritten,
            ["note"] = condebug == false
                ? "-condebug is NOT on the server's command line, so engine output isn't captured: engine_log and the events stream miss it. Add -condebug to the srcds launch options."
                : "engine_log and the events stream read the server's console.log over ssh.",
        };

        return HostToolHelpers.Ok(new JsonObject
        {
            ["ok"] = true,
            ["remote"] = remoteNode,
            ["gmod"] = gmodNode,
            ["bridge"] = bridgeNode,
            ["engine_log"] = engineNode,
        }.ToJsonString());
    }

    private static void FillPing(JsonObject node, BridgePingResult p, string unreachableHint)
    {
        node["reachable"] = p.Reachable;
        node["latency_ms"] = p.LatencyMs;
        node["enabled"] = p.Enabled;
        node["map"] = p.Map;
        node["maxplayers"] = p.MaxPlayers;
        node["singleplayer"] = p.SinglePlayer;
        node["bootstrap_pending"] = p.BootstrapPending;
        node["bootstrap_error"] = p.BootstrapError;
        if (p.BootstrapError != null)
        {
            node["hint"] = p.BootstrapError;
        }
        else if (!p.Reachable)
        {
            node["hint"] = unreachableHint;
        }
        else if (p.BootstrapPending == true)
        {
            node["hint"] = "Bridge reachable but the host_launch bootstrap is still in progress (workshop mount or post-mount map transition).";
        }
        else if (p.Enabled == false)
        {
            node["hint"] = "Bridge reachable but mcp_enable is 0. Run `mcp_enable 1` in the GMod console to allow tool dispatch.";
        }
    }

    // Capabilities: prefer the live convar values carried on the ping (so a convar
    // flipped after registration reads correctly); fall back to the manifest snapshot
    // when GMod is down or the ping carried none (older addon build).
    private static JsonArray Capabilities(MergedManifest manifest, BridgePingResult? ping)
    {
        var capabilities = new JsonArray();
        foreach (var cap in manifest.Capabilities.Values)
        {
            var current = cap.Current;
            if (ping is { Capabilities: { } liveCaps } && liveCaps.TryGetValue(cap.Id, out var liveVal))
            {
                current = liveVal;
            }
            capabilities.Add(new JsonObject
            {
                ["id"] = cap.Id,
                ["convar"] = cap.ConVar,
                ["current"] = current,
                ["default"] = cap.Default,
            });
        }
        return capabilities;
    }
}
