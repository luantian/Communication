using System.Net;
using System.Net.Sockets;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using NModbus;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class ModbusUdpIntegrationTests : IAsyncDisposable
{
    private readonly UdpClient _slaveUdp = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private readonly List<CommunicationHost> _hosts = [];

    public ModbusUdpIntegrationTests()
    {
        var port = ((IPEndPoint)_slaveUdp.Client.LocalEndPoint!).Port;
        Port = port;
        var factory = new ModbusFactory();
        var network = factory.CreateSlaveNetwork(_slaveUdp);
        network.AddSlave(factory.CreateSlave(1, new TestSlaveDataStore()));
        _listenTask = network.ListenAsync(_cts.Token);
    }

    public int Port { get; }

    private IPlcClient CreateClient()
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": 2000, "connectRetries": 0 },
                  "devices": [ { "name": "mb", "protocol": "ModbusUdp",
                                 "connection": { "ip": "127.0.0.1", "port": {{Port}}, "unitId": 1 } } ]
                }
                """),
            r => r.AddModbusUdp());
        _hosts.Add(host);
        return host.GetClient("mb");
    }

    [Fact]
    public async Task Write_then_read_roundtrip_over_udp()
    {
        var client = CreateClient();

        var write = await client.WriteWordsAsync("HR10", [0xCAFE, 0xBABE]);
        Assert.True(write.Success, write.ToString());

        var read = await client.ReadWordsAsync("HR10", 2);
        Assert.True(read.Success, read.ToString());
        Assert.Equal(new ushort[] { 0xCAFE, 0xBABE }, read.Value);
    }

    [Fact]
    public async Task Typed_and_bit_roundtrip_over_udp()
    {
        var client = CreateClient();

        Assert.True((await client.WriteAsync<int>("HR50", -777)).Success);
        Assert.Equal(-777, (await client.ReadAsync<int>("HR50")).Value);

        Assert.True((await client.WriteBitsAsync("C8", [true, false])).Success);
        Assert.Equal(new[] { true, false }, (await client.ReadBitsAsync("C8", 2)).Value);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        _cts.Cancel();
        _slaveUdp.Dispose();
        try
        {
            await _listenTask;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        _cts.Dispose();
    }
}
