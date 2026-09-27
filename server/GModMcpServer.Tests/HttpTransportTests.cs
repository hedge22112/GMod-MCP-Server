using System.Net;
using System.Text;
using GModMcpServer.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GModMcpServer.Tests;

/// <summary>
/// End-to-end over the real Kestrel + Streamable HTTP stack that <c>--mcp</c> serves, with
/// the SDK's own HTTP client on the other end.
/// </summary>
public class HttpTransportTests
{
	private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(10);

	private string _dataPath = null!;
	private TempBridgeRoot _root = null!;
	private WebApplication _app = null!;
	private Uri _endpoint = null!;

	[SetUp]
	public async Task SetUp()
	{
		// ConfigureGModServices wants <game>/garrysmod/data; lay that out around the temp mcp root.
		_root = new TempBridgeRoot();
		_dataPath = Path.Combine(_root.McpRoot, "GarrysMod", "garrysmod", "data");
		Directory.CreateDirectory(_dataPath);

		var listen = new McpListen("http://127.0.0.1:0", McpListen.DefaultPath, "127.0.0.1");
		var args = new[] { "--data-path", _dataPath };
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
		builder.WebHost.UseUrls(listen.BaseUrl);
		Program.ConfigureGModServices(builder, args, http: true);

		_app = builder.Build();
		Program.MapGModMcp(_app, listen);
		await _app.StartAsync();
		_endpoint = new Uri(_app.Urls.First() + listen.Path);
	}

	[TearDown]
	public async Task TearDown()
	{
		await _app.StopAsync();
		await _app.DisposeAsync();
		_root.Dispose();
	}

	private Task<McpClient> ConnectAsync() => McpClient.CreateAsync(
		new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = _endpoint,
			TransportMode = HttpTransportMode.StreamableHttp,
		}, NullLoggerFactory.Instance));

	[Test]
	public async Task ListTools_OverHttp_ReturnsHostTools()
	{
		await using var client = await ConnectAsync();

		var tools = await client.ListToolsAsync();

		Assert.Multiple(() =>
		{
			Assert.That(tools.Select(t => t.Name), Does.Contain("host_status"));
			Assert.That(client.ServerCapabilities.Tools?.ListChanged, Is.True);
		});
	}

	[Test]
	public async Task ManifestChange_NotifiesEveryConnectedSession()
	{
		await using var first = await ConnectAsync();
		await using var second = await ConnectAsync();
		var firstNotified = WatchListChanged(first);
		var secondNotified = WatchListChanged(second);

		await File.WriteAllTextAsync(Path.Combine(_dataPath, "mcp", "manifest_server.json"), """
			{ "realm": "server", "generation": 1, "capabilities": [],
			  "functions": [ { "id": "lua_run", "description": "Run Lua.", "realm": "server",
			                   "schema": {"type":"object","properties":{},"required":[]}, "requires": [] } ] }
			""");

		var both = Task.WhenAll(firstNotified, secondNotified);
		Assert.That(await Task.WhenAny(both, Task.Delay(NotifyTimeout)), Is.SameAs(both),
			"Every HTTP session must get tools/list_changed, not just the first one to connect.");

		var tools = await second.ListToolsAsync();
		Assert.That(tools.Select(t => t.Name), Does.Contain("lua_run_sv"));
	}

	[Test]
	public async Task ForeignOrigin_IsRejected()
	{
		using var http = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
		{
			Content = new StringContent(
				"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
				Encoding.UTF8, "application/json"),
		};
		request.Headers.Add("Origin", "http://evil.example");
		request.Headers.Accept.ParseAdd("application/json");
		request.Headers.Accept.ParseAdd("text/event-stream");

		using var response = await http.SendAsync(request);

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
	}

	private static Task WatchListChanged(McpClient client)
	{
		var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
		{
			fired.TrySetResult();
			return ValueTask.CompletedTask;
		});
		return fired.Task;
	}
}
