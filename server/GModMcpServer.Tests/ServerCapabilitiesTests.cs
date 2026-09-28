using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace GModMcpServer.Tests;

public class ServerCapabilitiesTests
{
    // Guards the regression where the server emitted notifications/tools/list_changed
    // without advertising the capability, so spec-compliant clients ignored it.
    [TestCase(false)]
    [TestCase(true)]
    public void Server_AdvertisesToolsListChanged(bool http)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Program.AddGModMcpServer(services, http);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.That(options.Capabilities?.Tools?.ListChanged, Is.True,
            "Server must advertise tools.listChanged so clients honour the " +
            "notifications/tools/list_changed emitted on manifest changes.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Instructions_MentionDedicatedControl_OnlyWhenConfigured(bool configured)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configured
            ? new GModMcpServer.Host.DedicatedServer("systemctl start gmod", null, null)
            : new GModMcpServer.Host.DedicatedServer(null, null, null));
        Program.AddGModMcpServer(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.That(options.ServerInstructions, configured ? Does.Contain("host_rcon") : Does.Not.Contain("host_rcon"));
    }
}
