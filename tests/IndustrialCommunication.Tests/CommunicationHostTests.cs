using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.Tests.TestSupport;
using Xunit;

namespace IndustrialCommunication.Tests;

public class CommunicationHostTests
{
    private static CommunicationConfig Config(string json) => CommunicationConfigLoader.Parse(json);

    [Fact]
    public async Task GetClient_builds_fake_driver_with_typed_options_and_defaults()
    {
        var config = Config("""
            {
              "defaults": { "timeoutMs": 2345 },
              "devices": [ { "name": "fake-1", "protocol": "Fake",
                             "connection": { "token": "abc", "dataLayout": "CDAB" } } ]
            }
            """);

        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (options, device, ctx) => new FakePlcClient(options, device, ctx)));

        var client = (FakePlcClient)host.GetClient("fake-1");

        Assert.Equal("abc", client.ResolvedOptions.Token);
        Assert.Equal(DataLayout.CDAB, client.ResolvedOptions.DataLayout);
        Assert.Equal(2345, client.ResolvedRuntime.TimeoutMs);
        Assert.Equal("fake-1", client.DeviceName);
    }

    [Fact]
    public async Task GetClient_caches_by_name_and_ignores_case()
    {
        var config = Config("""{ "devices": [ { "name": "dev", "protocol": "Fake" } ] }""");
        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        Assert.Same(host.GetClient("dev"), host.GetClient("DEV"));
    }

    [Fact]
    public async Task GetClient_unknown_protocol_lists_registered_drivers()
    {
        var config = Config("""{ "devices": [ { "name": "d", "protocol": "Nope" } ] }""");
        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        var ex = Assert.Throws<CommunicationException>(() => host.GetClient("d"));
        Assert.Contains("Nope", ex.Message);
        Assert.Contains("Fake", ex.Message);
    }

    [Fact]
    public async Task GetClient_unknown_and_disabled_devices_throw()
    {
        var config = Config("""
            { "devices": [
                { "name": "off", "protocol": "Fake", "enabled": false, "connection": {} },
                { "name": "on", "protocol": "Fake", "connection": {} } ] }
            """);
        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        Assert.Equal("on", Assert.Single(host.DeviceNames));
        var disabled = Assert.Throws<CommunicationException>(() => host.GetClient("off"));
        Assert.Contains("disabled", disabled.Message);
        var missing = Assert.Throws<CommunicationException>(() => host.GetClient("who"));
        Assert.Contains("Unknown device", missing.Message);
    }

    [Fact]
    public async Task Missing_connection_node_falls_back_to_default_options()
    {
        var config = Config("""{ "devices": [ { "name": "d", "protocol": "Fake" } ] }""");
        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        var client = (FakePlcClient)host.GetClient("d");

        Assert.Null(client.ResolvedOptions.Token);
        Assert.Equal(DataLayout.ABCD, client.ResolvedOptions.DataLayout);
    }

    [Fact]
    public async Task ConnectAll_reports_per_device_outcome()
    {
        var config = Config("""
            { "defaults": { "connectRetries": 0 },
              "devices": [
                { "name": "good", "protocol": "Fake", "connection": { "failConnect": false } },
                { "name": "bad", "protocol": "Fake", "connection": { "failConnect": true } },
                { "name": "off", "protocol": "Fake", "enabled": false, "connection": {} } ] }
            """);
        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));

        var results = await host.ConnectAllAsync();

        Assert.Equal(2, results.Count);
        Assert.True(results["good"].Success);
        Assert.False(results["bad"].Success);
        Assert.Equal(CommErrorKind.ConnectionLost, results["bad"].Kind);
        Assert.DoesNotContain("off", results.Keys);
    }

    [Fact]
    public async Task Lazy_reconnect_recovers_after_connect_failure()
    {
        // Defaults make the first ConnectAll attempt fail, then an operation transparently reconnects
        // after the fake transport is switched back to healthy.
        var config = Config("""
            { "defaults": { "connectRetries": 0, "timeoutMs": 500 },
              "devices": [ { "name": "d", "protocol": "Fake", "connection": { "failConnect": true } } ] }
            """);
        await using var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));
        var client = (FakePlcClient)host.GetClient("d");

        var failed = await client.ReadWordsAsync("HR:10", 2);
        Assert.False(failed.Success);
        Assert.Equal(CommErrorKind.ConnectionLost, failed.Kind);

        client.ResolvedOptions.FailConnect = false;
        var ok = await client.ReadWordsAsync("HR:10", 2);
        Assert.True(ok.Success);
        // The fake reads back written values; verify through the reconnected client.
        Assert.True((await client.WriteWordsAsync("HR:10", [10, 11])).Success);
        var readBack = await client.ReadWordsAsync("HR:10", 2);
        Assert.True(readBack.Success);
        Assert.Equal(new ushort[] { 10, 11 }, readBack.Value);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_disconnects_created_clients()
    {
        var config = Config("""{ "devices": [ { "name": "d", "protocol": "Fake", "connection": {} } ] }""");
        var host = CommunicationHost.FromConfig(config, r => r
            .Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));
        var client = (FakePlcClient)host.GetClient("d");
        await client.ConnectAsync();

        await host.DisposeAsync();

        Assert.Equal(1, client.DisconnectCount);
        Assert.Throws<ObjectDisposedException>(() => host.GetClient("d"));
    }
}
