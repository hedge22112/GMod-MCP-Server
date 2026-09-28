using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace GModMcpServer.Host.Tools;

public sealed class RconTool : IHostTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DedicatedServer _dedicated;

    public RconTool(DedicatedServer dedicated)
    {
        _dedicated = dedicated;
    }

    public string Name => "host_rcon";

    public string Description =>
        "Run a console command on a dedicated server over Source RCON and return its output. It doesn't go " +
        "through the MCP bridge, so it still works when the bridge is down (mcp_enable 0, Lua errors at " +
        "boot), e.g. to run `mcp_enable 1`, `mcp_allow_<cap> 1`, `status` or `changelevel`. Only available " +
        "when the MCP server was started with --rcon host[:port] and the password in MCP_RCON_PASSWORD. " +
        "Anyone with RCON already has full control of the server, so this is not capability-gated.";

    public JsonElement InputSchema { get; } = HostToolHelpers.ParseSchema("""
    {
      "type": "object",
      "properties": {
        "command": { "type": "string", "description": "The console command line to run, e.g. \"status\"." }
      },
      "required": ["command"]
    }
    """);

    public async ValueTask<CallToolResult> InvokeAsync(IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        if (_dedicated.Rcon is not { } rcon)
        {
            return HostToolHelpers.Err(new JsonObject
            {
                ["ok"] = false,
                ["error"] = "RCON isn't configured. Start the MCP server with --rcon host[:port] and the password in "
                    + "the MCP_RCON_PASSWORD environment variable, and set rcon_password on the server.",
            }.ToJsonString());
        }

        var command = HostToolHelpers.GetString(args, "command", "");
        if (string.IsNullOrWhiteSpace(command))
        {
            return HostToolHelpers.Err(new JsonObject { ["ok"] = false, ["error"] = "`command` is required" }.ToJsonString());
        }

        try
        {
            var quitting = command.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)
                || command.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase);
            var output = await rcon.ExecuteAsync(command, Timeout, ct, expectDisconnect: quitting).ConfigureAwait(false);
            return HostToolHelpers.Ok(new JsonObject
            {
                ["ok"] = true,
                ["endpoint"] = rcon.Endpoint,
                ["output"] = output,
            }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return HostToolHelpers.Err(new JsonObject
            {
                ["ok"] = false,
                ["endpoint"] = rcon.Endpoint,
                ["error"] = ex is OperationCanceledException ? $"RCON at {rcon.Endpoint} didn't answer within {Timeout.TotalSeconds:F0}s" : ex.Message,
            }.ToJsonString());
        }
    }
}
