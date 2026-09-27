namespace GModMcpServer.Tests;

public class McpListenTests
{
	[Test]
	public void Parse_BarePort_BindsLoopbackAtDefaultPath()
	{
		var listen = McpListen.Parse("5123");

		Assert.Multiple(() =>
		{
			Assert.That(listen.BaseUrl, Is.EqualTo("http://127.0.0.1:5123"));
			Assert.That(listen.Path, Is.EqualTo("/mcp"));
			Assert.That(listen.Endpoint, Is.EqualTo("http://127.0.0.1:5123/mcp"));
		});
	}

	[TestCase("http://0.0.0.0:5123", "http://0.0.0.0:5123", "/mcp")]
	[TestCase("http://localhost:5123/", "http://localhost:5123", "/mcp")]
	[TestCase("http://127.0.0.1:5123/gmod/", "http://127.0.0.1:5123", "/gmod")]
	[TestCase("https://[::1]:8443/x", "https://[::1]:8443", "/x")]
	public void Parse_Url_SplitsBaseAndPath(string spec, string baseUrl, string path)
	{
		var listen = McpListen.Parse(spec);

		Assert.Multiple(() =>
		{
			Assert.That(listen.BaseUrl, Is.EqualTo(baseUrl));
			Assert.That(listen.Path, Is.EqualTo(path));
		});
	}

	[TestCase("0")]
	[TestCase("70000")]
	[TestCase("localhost:5123")]
	[TestCase("ftp://127.0.0.1:21")]
	[TestCase("http://*:5123")]
	public void Parse_Invalid_Throws(string spec)
	{
		Assert.Throws<ArgumentException>(() => McpListen.Parse(spec));
	}

	[TestCase(null, true)]
	[TestCase("", true)]
	[TestCase("http://localhost:3000", true)]
	[TestCase("http://127.0.0.1:5123", true)]
	[TestCase("http://[::1]:5123", true)]
	[TestCase("http://evil.example", false)]
	[TestCase("null", false)]
	public void IsAllowedOrigin_LoopbackBind(string? origin, bool allowed)
	{
		Assert.That(McpListen.Parse("5123").IsAllowedOrigin(origin), Is.EqualTo(allowed));
	}

	[Test]
	public void IsAllowedOrigin_NamedBind_AllowsThatHost()
	{
		var listen = McpListen.Parse("http://gmod-box:5123");

		Assert.Multiple(() =>
		{
			Assert.That(listen.IsAllowedOrigin("http://gmod-box:5123"), Is.True);
			Assert.That(listen.IsAllowedOrigin("http://evil.example"), Is.False);
		});
	}

	[Test]
	public void IsAllowedOrigin_WildcardBind_AllowsOnlyLoopback()
	{
		var listen = McpListen.Parse("http://0.0.0.0:5123");

		Assert.Multiple(() =>
		{
			Assert.That(listen.IsAllowedOrigin("http://127.0.0.1:5123"), Is.True);
			Assert.That(listen.IsAllowedOrigin("http://0.0.0.0:5123"), Is.False);
		});
	}
}
