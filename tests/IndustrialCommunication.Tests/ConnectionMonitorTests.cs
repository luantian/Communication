using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.Tests.TestSupport;
using Xunit;

namespace IndustrialCommunication.Tests;

public class ConnectionMonitorTests
{
    // The monitor ticks on a 10 ms interval; under parallel test load a generous budget avoids flakiness.
    private static async Task UntilAsync(Func<bool> probe, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (probe())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not met in time.");
    }

    private static CommunicationHost CreateHost(string json) =>
        CommunicationHost.FromConfig(CommunicationConfigLoader.Parse(json),
            r => r.Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

    [Fact]
    public async Task Monitor_connects_disconnected_clients_and_recovers_after_failure()
    {
        var host = CreateHost("""
            {
              "defaults": { "connectRetries": 0, "connectionMonitor": { "enabled": true, "intervalMs": 10 } },
              "devices": [ { "name": "d", "protocol": "Fake", "connection": { "failConnect": false } } ]
            }
            """);

        await using (host)
        {
            var client = (FakePlcClient)host.GetClient("d");

            // The monitor auto-started (enabled in defaults) and connects the client.
            await UntilAsync(() => client.IsConnected);
            var connectCount = client.ConnectCount;

            // Simulate a dropped connection: fail connects, force disconnect.
            client.ResolvedOptions.FailConnect = true;
            await client.DisconnectAsync();
            Assert.False(client.IsConnected);

            await UntilAsync(() => client.ConnectCount > connectCount); // monitor keeps retrying
            Assert.False(client.IsConnected);

            // The PLC comes back — the monitor restores the connection.
            client.ResolvedOptions.FailConnect = false;
            await UntilAsync(() => client.IsConnected);
        }
    }

    [Fact]
    public async Task Monitor_stops_with_the_host()
    {
        var host = CreateHost("""
            {
              "defaults": { "connectRetries": 0, "connectionMonitor": { "enabled": true, "intervalMs": 10 } },
              "devices": [ { "name": "d", "protocol": "Fake", "connection": {} } ]
            }
            """);

        var client = (FakePlcClient)host.GetClient("d");
        await UntilAsync(() => client.IsConnected);

        await host.DisposeAsync();

        var connects = client.ConnectCount;
        await Task.Delay(100);
        Assert.Equal(connects, client.ConnectCount);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Explicit_start_without_config_enabled()
    {
        var host = CreateHost("""
            { "defaults": { "connectRetries": 0 }, "devices": [ { "name": "d", "protocol": "Fake", "connection": {} } ] }
            """);

        await using (host)
        {
            var client = (FakePlcClient)host.GetClient("d");
            host.StartConnectionMonitor();
            await UntilAsync(() => client.IsConnected);

            // Second start is a no-op.
            host.StartConnectionMonitor();
        }
    }
}
