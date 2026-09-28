using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace GModMcpServer.Host;

/// <summary>
/// How this host starts, stops and talks to a dedicated server it runs next to (typically with
/// <c>--mcp</c>, on the server's own machine). <c>host_launch</c> and <c>host_close</c> can't drive
/// srcds the way they drive a local gmod.exe, so the operator supplies the commands:
/// <c>--server-start</c> / <c>--server-stop</c> (shell commands, e.g. <c>systemctl start gmod</c>)
/// and <c>--rcon host[:port]</c> with the password in <c>MCP_RCON_PASSWORD</c>.
/// </summary>
public sealed partial class DedicatedServer
{
    private const int MaxOutput = 4000;

    public DedicatedServer(string? startCommand, string? stopCommand, RconClient? rcon)
    {
        StartCommand = string.IsNullOrWhiteSpace(startCommand) ? null : startCommand;
        StopCommand = string.IsNullOrWhiteSpace(stopCommand) ? null : stopCommand;
        Rcon = rcon;
    }

    public string? StartCommand { get; }

    public string? StopCommand { get; }

    public RconClient? Rcon { get; }

    public bool Configured => StartCommand is not null || StopCommand is not null || Rcon is not null;

    public static DedicatedServer FromConfig(IConfiguration cfg)
    {
        var rconSpec = cfg["rcon"];
        RconClient? rcon = null;
        if (!string.IsNullOrWhiteSpace(rconSpec))
        {
            // Not a command-line argument, so it stays out of process listings.
            var password = cfg["RCON_PASSWORD"];
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("--rcon needs the RCON password in the MCP_RCON_PASSWORD environment variable.");
            }
            rcon = RconClient.Parse(rconSpec, password);
        }
        return new DedicatedServer(cfg["server-start"], cfg["server-stop"], rcon);
    }

    /// <summary>
    /// Fill <c>{map}</c>, <c>{gamemode}</c> and <c>{maxplayers}</c> in the start command. Values
    /// land in a shell command line, so only plain map-name characters are allowed.
    /// </summary>
    public static string Expand(string command, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            var token = "{" + key + "}";
            if (!command.Contains(token, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"the start command uses {token} but no {key} was given");
            if (!SafeValue().IsMatch(value))
                throw new ArgumentException($"{key} '{value}' has characters that aren't allowed in the start command");
            command = command.Replace(token, value, StringComparison.Ordinal);
        }
        return command;
    }

    /// <summary>
    /// Run a shell command and return its exit code and output. The output goes to a temp file
    /// rather than a pipe, so a command that leaves a server running in the background (nohup,
    /// screen, tmux) still returns: the server inherits the file, not our pipe.
    /// </summary>
    public static async Task<ShellResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "gmodmcp-" + Guid.NewGuid().ToString("N") + ".log");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"({command}) >\"{outFile}\" 2>&1 <NUL\"" };
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"({command}) >'{outFile}' 2>&1 </dev/null");
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        // Keep our stdio (the MCP stdio transport) away from the command.
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var proc = Process.Start(psi) ?? throw new IOException("could not start the shell");
        proc.StandardInput.Close();
        _ = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }

        var output = "";
        try
        {
            output = await File.ReadAllTextAsync(outFile, CancellationToken.None).ConfigureAwait(false);
            File.Delete(outFile);
        }
        catch { /* no output */ }
        if (output.Length > MaxOutput) output = "..." + output[^MaxOutput..];

        return new ShellResult(timedOut ? null : proc.ExitCode, output.Trim(), timedOut);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SafeValue();
}

public sealed record ShellResult(int? ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
