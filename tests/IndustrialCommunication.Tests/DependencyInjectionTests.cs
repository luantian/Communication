using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.DependencyInjection;
using IndustrialCommunication.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndustrialCommunication.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public async Task Factory_resolves_clients_and_container_disposes_the_host()
    {
        var services = new ServiceCollection();
        services.AddIndustrialCommunication(
            CommunicationConfigLoader.Parse("""
                { "defaults": { "connectRetries": 0 },
                  "devices": [ { "name": "d", "protocol": "Fake", "connection": {} } ] }
                """),
            r => r.Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        await using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IPlcClientFactory>();
        Assert.Equal(["d"], factory.DeviceNames);

        var client = (FakePlcClient)factory.GetClient("d");
        Assert.Same(client, factory.GetClient("d"));

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // Disposing the container disposes the host, which disconnects every client.
        await provider.DisposeAsync();
        Assert.Equal(1, client.DisconnectCount);
    }

    [Fact]
    public void Same_host_instance_is_resolved_twice()
    {
        var services = new ServiceCollection();
        services.AddIndustrialCommunication(
            CommunicationConfigLoader.Parse("""
                { "devices": [ { "name": "d", "protocol": "Fake", "connection": {} } ] }
                """),
            r => r.Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<CommunicationHost>(),
            provider.GetRequiredService<CommunicationHost>());
    }
}
