using System.Text.Json;
using System.Text.Json.Nodes;
using GModMcpServer.Bridge;
using ModelContextProtocol.Protocol;

namespace GModMcpServer.Host.Tools;

public sealed class CloseTool : IHostTool
{
    private readonly GameProcessManager _proc;
    private readonly DedicatedServer _dedicated;
    private readonly BridgePinger _pinger;

    public CloseTool(GameProcessManager proc, DedicatedServer dedicated, BridgePinger pinger)
    {
        _proc = proc;
        _dedicated = dedicated;
        _pinger = pinger;
    }

    public string Name => "host_close";

    public string Description =>
        "Close the running GMod process (located by name, regardless of who launched it). " +
        "By default does a clean shutdown — posts the window-close signal so GMod saves its " +
        "config, which is the only way capability grants (mcp_allow_*) and mcp_enable set this " +
        "session persist to the next launch — waiting up to graceful_seconds before falling back " +
        "to a kill. Pass force=true to skip straight to killing the process tree (faster, but the " +
        "config is not saved so this-session grants are lost). " +
        "With a dedicated server next to the MCP server, it runs the --server-stop command, or failing that " +
        "sends `quit` over --rcon (a clean shutdown that saves the server's config), then waits for the bridge " +
        "to go quiet; force is then ignored.";

    public JsonElement InputSchema { get; } = HostToolHelpers.ParseSchema("""
    {
      "type": "object",
      "properties": {
        "force":            { "type": "boolean", "description": "Kill the process tree immediately instead of a clean shutdown. Faster, but GMod won't save its config — capability grants and mcp_enable set this session are lost (default: false)." },
        "graceful_seconds": { "type": "number",  "description": "How long to wait for the clean shutdown to finish before falling back to a kill (default: 10). Ignored when force=true." }
      },
      "required": []
    }
    """);

    public async ValueTask<CallToolResult> InvokeAsync(IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        if (_dedicated.StopCommand is not null || _dedicated.Rcon is not null)
        {
            return await CloseDedicatedAsync(args, ct).ConfigureAwait(false);
        }
        return Close(args);
    }

    private CallToolResult Close(IDictionary<string, JsonElement>? args)
    {
        var force = HostToolHelpers.GetBool(args, "force", false);
        var seconds = 10.0;
        if (args is not null && args.TryGetValue("graceful_seconds", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var s))
        {
            seconds = Math.Max(0, s);
        }

        var method = force ? _proc.Close(TimeSpan.Zero) : _proc.Close(TimeSpan.FromSeconds(seconds));

        if (method == CloseMethod.NotRunning)
        {
            var notRunning = new JsonObject { ["ok"] = true, ["closed"] = false, ["reason"] = "no gmod.exe process is currently running" };
            return HostToolHelpers.Ok(notRunning.ToJsonString());
        }

        var clean = method == CloseMethod.CleanWindowClose;
        var result = new JsonObject
        {
            ["ok"] = true,
            ["closed"] = true,
            ["method"] = method switch
            {
                CloseMethod.CleanWindowClose => "clean",
                CloseMethod.KilledAfterTimeout => "killed_after_timeout",
                _ => "killed",
            },
            ["config_saved"] = clean,
        };
        if (method == CloseMethod.KilledAfterTimeout)
        {
            result["note"] = "Clean shutdown didn't finish within graceful_seconds; killed. GMod config (capability grants) may not have been saved.";
        }
        else if (method == CloseMethod.Killed)
        {
            result["note"] = force
                ? "Force-killed as requested; GMod config was not saved, so capability grants set this session won't persist."
                : "Killed without a clean shutdown; GMod config was not saved, so capability grants set this session won't persist.";
        }
        return HostToolHelpers.Ok(result.ToJsonString());
    }

    /// <summary>
    /// Stop a dedicated server with --server-stop, or with <c>quit</c> over RCON when there's no stop
    /// command, then wait up to graceful_seconds for the bridge to stop answering.
    /// </summary>
    private async Task<CallToolResult> CloseDedicatedAsync(IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var seconds = 10.0;
        if (args is not null && args.TryGetValue("graceful_seconds", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var s))
        {
            seconds = Math.Max(0, s);
        }

        var result = new JsonObject { ["dedicated"] = true };
        if (_dedicated.StopCommand is { } stop)
        {
            var run = await DedicatedServer.RunAsync(stop, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            result["method"] = "stop_command";
            result["command"] = stop;
            result["exit_code"] = run.ExitCode;
            result["output"] = run.Output.Length == 0 ? null : run.Output;
            if (!run.Succeeded)
            {
                result["ok"] = false;
                result["error"] = run.TimedOut ? "The stop command didn't return within 60s." : $"The stop command exited with code {run.ExitCode}.";
                return HostToolHelpers.Err(result.ToJsonString());
            }
        }
        else
        {
            var rcon = _dedicated.Rcon!;
            result["method"] = "rcon_quit";
            result["endpoint"] = rcon.Endpoint;
            try
            {
                await rcon.ExecuteAsync("quit", TimeSpan.FromSeconds(10), ct, expectDisconnect: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                result["ok"] = false;
                result["error"] = "RCON quit failed: " + (ex is OperationCanceledException ? "no answer within 10s" : ex.Message);
                return HostToolHelpers.Err(result.ToJsonString());
            }
        }

        // The bridge going quiet is the sign the server is actually down.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        var stopped = false;
        while (true)
        {
            var ping = await _pinger.PingAsync("server", TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (!ping.Reachable) { stopped = true; break; }
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        result["ok"] = true;
        result["closed"] = stopped;
        if (!stopped)
        {
            result["note"] = $"The server's bridge was still answering after {seconds:F0}s; it may still be shutting down. Check host_status.";
        }
        return HostToolHelpers.Ok(result.ToJsonString());
    }
}
