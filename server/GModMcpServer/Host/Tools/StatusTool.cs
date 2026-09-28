using System.Text.Json;
using System.Text.Json.Nodes;
using GModMcpServer.Bridge;
using ModelContextProtocol.Protocol;

namespace GModMcpServer.Host.Tools;

public sealed class StatusTool : IHostTool
{
    private readonly GameProcessManager _proc;
    private readonly ManifestWatcher _manifest;
    private readonly BridgePinger _pinger;
    private readonly EngineLog _engineLog;
    private readonly DedicatedServer _dedicated;

    public StatusTool(GameProcessManager proc, ManifestWatcher manifest, BridgePinger pinger, EngineLog engineLog, DedicatedServer dedicated)
    {
        _proc = proc;
        _manifest = manifest;
        _pinger = pinger;
        _engineLog = engineLog;
        _dedicated = dedicated;
    }

    public string Name => "host_status";

    public string Description =>
        "Report whether GMod is running, whether the MCP bridge is reachable (a live ping is " +
        "sent when GMod is detected), and the current tool count and capability state. " +
        "Useful for diagnosing why a tool call isn't working. With a dedicated server configured " +
        "(--server-start/--server-stop/--rcon) it always pings, since srcds isn't a local gmod.exe, and " +
        "reports that configuration under `dedicated_control`.";

    public JsonElement InputSchema { get; } = HostToolHelpers.ParseSchema("""
    { "type": "object", "properties": {}, "required": [] }
    """);

    public async ValueTask<CallToolResult> InvokeAsync(IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var snap = _proc.Snapshot();
        var manifest = _manifest.Current;

        var bridgeNode = new JsonObject
        {
            ["tools"] = manifest.Tools.Count,
        };

        BridgePingResult? ping = null;
        // A dedicated server runs as srcds, not gmod.exe, so the process check can't gate the ping.
        if (snap.Running || _dedicated.Configured)
        {
            var p = await _pinger.PingAsync(ct).ConfigureAwait(false);
            ping = p;
            bridgeNode["reachable"] = p.Reachable;
            bridgeNode["latency_ms"] = p.LatencyMs;
            bridgeNode["enabled"] = p.Enabled;
            bridgeNode["map"] = p.Map;
            bridgeNode["maxplayers"] = p.MaxPlayers;
            bridgeNode["singleplayer"] = p.SinglePlayer;
            bridgeNode["bootstrap_pending"] = p.BootstrapPending;
            bridgeNode["bootstrap_error"] = p.BootstrapError;
            bridgeNode["dedicated"] = p.Dedicated;
            if (p.BootstrapError != null)
            {
                bridgeNode["hint"] = p.BootstrapError;
            }
            else if (!p.Reachable && !snap.Running)
            {
                bridgeNode["hint"] = "The dedicated server's bridge didn't respond: the server may be stopped (host_launch starts it), still loading, or have mcp_enable 0 (host_rcon can set it).";
            }
            else if (!p.Reachable)
            {
                bridgeNode["hint"] = "GMod is running but the bridge didn't respond — likely still loading, or paused on a menu.";
            }
            else if (p.BootstrapPending == true)
            {
                bridgeNode["hint"] = "Bridge reachable but the host_launch bootstrap is still in progress (workshop mount or post-mount map transition).";
            }
            else if (p.Enabled == false)
            {
                bridgeNode["hint"] = "Bridge reachable but mcp_enable is 0. Run `mcp_enable 1` in the GMod console to allow tool dispatch.";
            }
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

        // Capabilities: prefer the live convar values carried on the ping (so a convar
        // flipped after registration reads correctly); fall back to the manifest snapshot
        // when GMod is down or the ping carried none (older addon build).
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
        bridgeNode["capabilities"] = capabilities;

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
        if (!snap.Running && _dedicated.Configured)
            engineNote = recentlyWritten
                ? "Dedicated server: console.log is being written, so -condebug is on and engine_log / the events stream work."
                : "Dedicated server: console.log isn't being written. Add -condebug to the srcds command line for engine_log and the events stream.";
        else if (!snap.Running)
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
            ["capturing"] = snap.Running ? (condebug ?? recentlyWritten) : _dedicated.Configured ? recentlyWritten : (bool?)null,
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
        if (_dedicated.Configured)
        {
            result["dedicated_control"] = new JsonObject
            {
                ["start_command"] = _dedicated.StartCommand,
                ["stop_command"] = _dedicated.StopCommand,
                ["rcon"] = _dedicated.Rcon?.Endpoint,
            };
        }

        return HostToolHelpers.Ok(result.ToJsonString());
    }
}
