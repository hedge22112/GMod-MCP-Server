using System.Text.Json;
using System.Text.Json.Nodes;
using GModMcpServer.Bridge;
using GModMcpServer.Host;
using GModMcpServer.Remote;
using GModMcpServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GModMcpServer.Tests;

/// <summary>
/// The ssh transport against the real <c>agent.sh</c>, with a local bash standing in for
/// ssh (the agent only ever sees a shell command and stdio, so that's all ssh adds).
/// Needs bash 4+ with coreutils: /bin/bash on Linux CI, Git Bash on Windows; skipped
/// where neither exists.
/// </summary>
public class SshRemoteTests
{
	[TestCase("gmodbox", "gmodbox", null)]
	[TestCase("peter@gmodbox", "peter@gmodbox", null)]
	[TestCase("gmodbox:/srv/gmod/garrysmod/data", "gmodbox", "/srv/gmod/garrysmod/data")]
	[TestCase("peter@gmodbox:~/serverfiles/garrysmod/data", "peter@gmodbox", "~/serverfiles/garrysmod/data")]
	[TestCase("gmodbox:", "gmodbox", null)]
	public void Parse_SplitsDestinationAndPath(string spec, string destination, string? path)
	{
		var target = SshTarget.Parse(spec, null);
		Assert.Multiple(() =>
		{
			Assert.That(target.Destination, Is.EqualTo(destination));
			Assert.That(target.DataPath, Is.EqualTo(path));
		});
	}

	[Test]
	public void Parse_DataPathOverridesInlinePath()
	{
		var target = SshTarget.Parse("gmodbox:/a/garrysmod/data", "/b/garrysmod/data");
		Assert.That(target.DataPath, Is.EqualTo("/b/garrysmod/data"));
	}

	[Test]
	public void Parse_RejectsMissingHost()
	{
		Assert.Throws<ArgumentException>(() => SshTarget.Parse(":/srv/garrysmod/data", null));
	}

	[Test]
	public async Task Agent_ResolvesDataPath_AndRoundTripsABridgeCall()
	{
		using var env = RemoteEnv.Create();
		using var responder = new FakeGmodResponder(env.McpRoot, "server", req =>
			new JsonObject { ["ok"] = true, ["echo"] = req.Args?.DeepClone(), ["fn"] = req.FunctionId });
		var bridge = new SshBridge(env.Agent, "server", Guid.NewGuid().ToString("N"));

		var resp = await bridge.SendAsync("echo", Json("""{"text":"hi"}"""), TimeSpan.FromSeconds(10), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(env.Agent.DataPath, Does.EndWith("/garrysmod/data"));
			Assert.That(resp.Result?["fn"]?.GetValue<string>(), Is.EqualTo("echo"));
			Assert.That(resp.Result?["echo"]?["text"]?.GetValue<string>(), Is.EqualTo("hi"));
			Assert.That(Directory.GetFiles(Path.Combine(env.McpRoot, "server", "in")), Is.Empty);
		});
		Assert.That(() => Directory.GetFiles(Path.Combine(env.McpRoot, "server", "out")),
			Is.Empty.After(3000, 50), "the response file is removed once read");
	}

	[Test]
	public void Bridge_TimesOutAsTaskCanceled_WhenNothingAnswers()
	{
		using var env = RemoteEnv.Create();
		var bridge = new SshBridge(env.Agent, "server", Guid.NewGuid().ToString("N"));

		Assert.ThrowsAsync<TaskCanceledException>(() =>
			bridge.SendAsync("_ping", Json("{}"), TimeSpan.FromMilliseconds(500), CancellationToken.None));
		Assert.That(() => Directory.GetFiles(Path.Combine(env.McpRoot, "server", "in")),
			Is.Empty.After(3000, 50), "an unanswered request is withdrawn");
	}

	[Test]
	public async Task ManifestWatcher_PicksUpRemoteManifestChanges()
	{
		using var env = RemoteEnv.Create();
		using var watcher = new ManifestWatcher(env.Agent, NullLogger<ManifestWatcher>.Instance);
		await watcher.WaitForInitialAsync(TimeSpan.FromSeconds(5));
		Assert.That(watcher.Current.Tools, Is.Empty);

		await File.WriteAllTextAsync(Path.Combine(env.McpRoot, "manifest_server.json"),
			"""{"realm":"server","functions":[{"id":"lua_run","description":"Run Lua."}],"capabilities":[]}""");

		Assert.That(() => watcher.Current.Tools.ContainsKey("lua_run_sv"), Is.True.After(5000, 100));
	}

	[Test]
	public void LogFile_ReadsRangesAndTail_AndReportsMissing()
	{
		using var env = RemoteEnv.Create();
		File.WriteAllText(Path.Combine(env.ModDir, "console.log"), "one\ntwo\nthree\n");
		var reader = new EngineLogReader(new RemoteLogFile(env.Agent, "../console.log"));

		long cursor = 4;
		var fromCursor = reader.ReadFrom(ref cursor);
		var (tail, end) = reader.ReadTail(1);
		var missing = new RemoteLogFile(env.Agent, "../nope.log").Read(0, 10);

		Assert.Multiple(() =>
		{
			Assert.That(fromCursor, Is.EqualTo(new[] { "two", "three" }));
			Assert.That(cursor, Is.EqualTo(14));
			Assert.That(tail, Is.EqualTo(new[] { "three" }));
			Assert.That(end, Is.EqualTo(14));
			Assert.That(missing, Is.Null);
		});
	}

	private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

	/// <summary>A temp <c>garrysmod/data</c> with a connected agent serving it.</summary>
	private sealed class RemoteEnv : IDisposable
	{
		private readonly string _root;

		private RemoteEnv(string root, SshAgent agent)
		{
			_root = root;
			Agent = agent;
		}

		public SshAgent Agent { get; }

		public string ModDir => Path.Combine(_root, "garrysmod");

		public string McpRoot => Path.Combine(_root, "garrysmod", "data", "mcp");

		public static RemoteEnv Create()
		{
			var bash = FindBash();
			if (bash is null) Assert.Ignore("no bash available to stand in for the remote host");

			var root = Path.Combine(Path.GetTempPath(), "gmod-mcp-ssh-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(root, "garrysmod", "data", "mcp"));

			var agent = new SshAgent(new SshTarget("test", ToBashPath(Path.Combine(root, "garrysmod", "data"))),
				bash!, (psi, remote) =>
				{
					// What sshd does with the command. It travels in the environment because
					// Git Bash re-parses a Windows command line differently from ssh.exe.
					psi.Environment["GMODMCP_REMOTE"] = remote;
					psi.ArgumentList.Add("-c");
					psi.ArgumentList.Add("eval \"$GMODMCP_REMOTE\"");
				}, NullLogger.Instance);
			agent.Start();
			var env = new RemoteEnv(root, agent);
			if (!agent.WaitFirstAttemptAsync(TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult())
			{
				env.Dispose();
				Assert.Fail("agent did not connect: " + agent.LastError);
			}
			return env;
		}

		private static string? FindBash()
		{
			if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;
			var git = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
			return File.Exists(git) ? git : null;
		}

		// Git Bash wants /c/Users/... rather than C:\Users\...
		private static string ToBashPath(string path)
		{
			if (!OperatingSystem.IsWindows()) return path;
			var full = Path.GetFullPath(path).Replace('\\', '/');
			return "/" + char.ToLowerInvariant(full[0]) + full[2..];
		}

		public void Dispose()
		{
			Agent.Dispose();
			try { Directory.Delete(_root, recursive: true); }
			catch { /* best effort */ }
		}
	}
}
