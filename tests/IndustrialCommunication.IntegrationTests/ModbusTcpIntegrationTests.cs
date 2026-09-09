using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class ModbusTcpIntegrationTests : IAsyncDisposable
{
    private static readonly bool[] Coils2To5 = [false, true, false, true];
    private static readonly bool[] Coils100To102 = [true, false, true];

    private readonly ModbusSlaveFixture _slave = new();

    private IPlcClient CreateClient(string extraJson = "")
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": 2000, "connectRetries": 0 },
                  "devices": [ { "name": "mb", "protocol": "ModbusTcp",
                                 "connection": { "ip": "127.0.0.1", "port": {{_slave.Port}}, "unitId": 1{{extraJson}} } } ]
                }
                """),
            r => r.AddModbusTcp());
        _hosts.Add(host);
        return host.GetClient("mb");
    }

    private readonly List<CommunicationHost> _hosts = [];

    [Fact]
    public async Task Read_words_returns_preset_slave_data()
    {
        _slave.DataStore.HoldingRegisters.WritePoints(10, [0x1234, 0x5678]);
        _slave.DataStore.InputRegisters.WritePoints(20, [0x00AA]);

        var client = CreateClient();
        var words = await client.ReadWordsAsync("HR10", 2);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, words.Value);
        Assert.Equal(0x00AA, (await client.ReadWordsAsync("IR20", 1)).Value[0]);
    }

    [Fact]
    public async Task Classic_and_named_addresses_point_to_the_same_register()
    {
        _slave.DataStore.HoldingRegisters.WritePoints(4, [0x0BB8]);

        var client = CreateClient();

        Assert.Equal(0x0BB8, (await client.ReadWordsAsync("40005", 1)).Value[0]);
        Assert.Equal(0x0BB8, (await client.ReadWordsAsync("HR4", 1)).Value[0]);
    }

    [Fact]
    public async Task Write_words_updates_slave_data_store()
    {
        var client = CreateClient();

        var write = await client.WriteWordsAsync("HR100", [0xCAFE, 0xBABE]);
        Assert.True(write.Success, write.ToString());

        Assert.Equal(new ushort[] { 0xCAFE, 0xBABE }, _slave.DataStore.HoldingRegisters.ReadPoints(100, 2));
    }

    [Fact]
    public async Task Typed_read_decodes_layout()
    {
        _slave.DataStore.HoldingRegisters.WritePoints(30, [0x3F80, 0x0000]);

        var client = CreateClient();

        var value = await client.ReadAsync<float>("HR30");
        Assert.True(value.Success, value.ToString());
        Assert.Equal(1.0f, value.Value);
    }

    [Fact]
    public async Task Typed_write_then_read_roundtrip()
    {
        var client = CreateClient();

        Assert.True((await client.WriteAsync<int>("HR50", -123456)).Success);
        Assert.Equal(-123456, (await client.ReadAsync<int>("HR50")).Value);
    }

    [Fact]
    public async Task Bits_read_and_write_coils()
    {
        _slave.DataStore.CoilDiscretes.WritePoints(2, Coils2To5);
        _slave.DataStore.CoilInputs.WritePoints(8, [true]);

        var client = CreateClient();

        var coils = await client.ReadBitsAsync("C2", 4);
        Assert.True(coils.Success, coils.ToString());
        Assert.Equal(Coils2To5, coils.Value);
        Assert.True((await client.ReadBitsAsync("DR8", 1)).Value[0]);

        Assert.True((await client.WriteBitsAsync("C100", Coils100To102)).Success);
        Assert.Equal(Coils100To102, _slave.DataStore.CoilDiscretes.ReadPoints(100, 3));
    }

    [Fact]
    public async Task String_write_then_read_roundtrip()
    {
        var client = CreateClient();

        Assert.True((await client.WriteStringAsync("HR200", "PUMP-1", 8)).Success);
        Assert.Equal("PUMP-1", (await client.ReadStringAsync("HR200", 8)).Value);
    }

    [Fact]
    public async Task Wrong_area_kind_is_rejected_without_traffic()
    {
        var client = CreateClient();
        await client.ConnectAsync();

        var words = await client.ReadWordsAsync("C10", 2);
        Assert.False(words.Success);
        Assert.Equal(CommErrorKind.InvalidArgument, words.Kind);

        var bits = await client.ReadBitsAsync("HR10", 2);
        Assert.False(bits.Success);
        Assert.Equal(CommErrorKind.InvalidArgument, bits.Kind);
    }

    [Fact]
    public async Task Invalid_address_reports_InvalidAddress()
    {
        var client = CreateClient();

        var result = await client.ReadWordsAsync("NOSUCH:10", 2);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.InvalidAddress, result.Kind);
    }

    [Fact]
    public async Task Group_read_reports_each_point()
    {
        _slave.DataStore.HoldingRegisters.WritePoints(60, [42]);
        var client = CreateClient();

        var group = await client.ReadGroupAsync(
        [
            new DevicePoint { Name = "count", Address = "HR60", ValueType = PlcValueType.UInt16 },
            new DevicePoint { Name = "temp", Address = "HR61", ValueType = PlcValueType.Float32 },
            new DevicePoint { Name = "run", Address = "C7", ValueType = PlcValueType.Bit },
            new DevicePoint { Name = "bad", Address = "WHAT", ValueType = PlcValueType.UInt16 },
        ]);

        Assert.Equal(42, group.GetValue<ushort>("count"));
        Assert.False(group.Statuses["bad"].Success);
        Assert.Equal(CommErrorKind.InvalidAddress, group.Statuses["bad"].Kind);
        Assert.False(group.AllSuccess);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _slave.DisposeAsync();
    }
}
