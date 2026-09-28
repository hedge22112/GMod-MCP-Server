using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GModMcpServer.Host;

namespace GModMcpServer.Tests;

public class DedicatedServerTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Test]
	public void Expand_FillsPlaceholders()
	{
		var cmd = DedicatedServer.Expand("./srcds_run +map {map} +gamemode {gamemode} +maxplayers {maxplayers}",
			new Dictionary<string, string?> { ["map"] = "gm_flatgrass", ["gamemode"] = "sandbox", ["maxplayers"] = "8" });

		Assert.That(cmd, Is.EqualTo("./srcds_run +map gm_flatgrass +gamemode sandbox +maxplayers 8"));
	}

	[Test]
	public void Expand_RejectsShellCharacters()
	{
		Assert.Throws<ArgumentException>(() => DedicatedServer.Expand("start {map}",
			new Dictionary<string, string?> { ["map"] = "gm_x; rm -rf ~" }));
	}

	[Test]
	public void Expand_LeavesUnusedPlaceholdersAlone()
	{
		Assert.That(DedicatedServer.Expand("systemctl start gmod",
			new Dictionary<string, string?> { ["map"] = "gm_x; bad" }), Is.EqualTo("systemctl start gmod"));
	}

	[Test]
	public async Task RunAsync_ReturnsExitCodeAndOutput()
	{
		var cmd = OperatingSystem.IsWindows() ? "echo hello && exit 3" : "echo hello; exit 3";

		var result = await DedicatedServer.RunAsync(cmd, Timeout, CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(result.ExitCode, Is.EqualTo(3));
			Assert.That(result.Output, Does.Contain("hello"));
			Assert.That(result.Succeeded, Is.False);
		});
	}

	[Test]
	public async Task RunAsync_ReturnsWhileABackgroundChildKeepsRunning()
	{
		if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX background syntax");

		// A start command that leaves the server running must not hang the tool.
		var started = DateTime.UtcNow;
		var result = await DedicatedServer.RunAsync("sleep 30 & echo started", Timeout, CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(result.Succeeded, Is.True);
			Assert.That(result.Output, Is.EqualTo("started"));
			Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(4)));
		});
	}

	[Test]
	public async Task RunAsync_TimesOut()
	{
		if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX sleep");

		var result = await DedicatedServer.RunAsync("sleep 30", TimeSpan.FromMilliseconds(300), CancellationToken.None);

		Assert.That(result.TimedOut, Is.True);
	}

	[TestCase("127.0.0.1", "127.0.0.1", 27015)]
	[TestCase("gmod.example:27016", "gmod.example", 27016)]
	[TestCase("[::1]:27017", "::1", 27017)]
	public void Rcon_Parse(string spec, string host, int port)
	{
		var rcon = RconClient.Parse(spec, "pw");

		Assert.Multiple(() =>
		{
			Assert.That(rcon.Host, Is.EqualTo(host));
			Assert.That(rcon.Port, Is.EqualTo(port));
		});
	}

	[Test]
	public async Task Rcon_ReturnsMultiPacketOutput()
	{
		using var server = new FakeRconServer("secret", cmd => cmd == "status" ? new[] { "hostname: test\n", "map: gm_construct\n" } : Array.Empty<string>());
		var rcon = new RconClient("127.0.0.1", server.Port, "secret");

		var output = await rcon.ExecuteAsync("status", Timeout, CancellationToken.None);

		Assert.That(output, Is.EqualTo("hostname: test\nmap: gm_construct\n"));
	}

	[Test]
	public void Rcon_BadPassword_Throws()
	{
		using var server = new FakeRconServer("secret", _ => Array.Empty<string>());
		var rcon = new RconClient("127.0.0.1", server.Port, "wrong");

		Assert.ThrowsAsync<UnauthorizedAccessException>(() => rcon.ExecuteAsync("status", Timeout, CancellationToken.None));
	}

	[Test]
	public async Task Rcon_Quit_ToleratesTheDisconnect()
	{
		using var server = new FakeRconServer("secret", _ => Array.Empty<string>(), closeAfterCommand: true);
		var rcon = new RconClient("127.0.0.1", server.Port, "secret");

		var output = await rcon.ExecuteAsync("quit", Timeout, CancellationToken.None, expectDisconnect: true);

		Assert.That(output, Is.Empty);
	}

	/// <summary>A minimal srcds RCON endpoint: auth, then one reply packet per output chunk.</summary>
	private sealed class FakeRconServer : IDisposable
	{
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly CancellationTokenSource _cts = new();

		public FakeRconServer(string password, Func<string, string[]> handle, bool closeAfterCommand = false)
		{
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_ = Task.Run(async () =>
			{
				using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
				var stream = client.GetStream();
				var (authId, _, pw) = await Read(stream);
				await Write(stream, authId, 0, "");
				await Write(stream, pw == password ? authId : -1, 2, "");
				if (pw != password) return;

				var (cmdId, _, cmd) = await Read(stream);
				if (closeAfterCommand) return;
				foreach (var chunk in handle(cmd)) await Write(stream, cmdId, 0, chunk);
				var (markerId, _, _) = await Read(stream);
				await Write(stream, markerId, 0, "");
			});
		}

		public int Port { get; }

		private async Task<(int, int, string)> Read(NetworkStream s)
		{
			var header = new byte[4];
			await s.ReadExactlyAsync(header, _cts.Token);
			var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
			await s.ReadExactlyAsync(body, _cts.Token);
			return (BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)),
				Encoding.UTF8.GetString(body, 8, body.Length - 10));
		}

		private async Task Write(NetworkStream s, int id, int type, string text)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			var packet = new byte[14 + bytes.Length];
			BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
			BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
			BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
			bytes.CopyTo(packet, 12);
			await s.WriteAsync(packet, _cts.Token);
		}

		public void Dispose()
		{
			_cts.Cancel();
			_listener.Stop();
		}
	}
}
