using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class KeyenceFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedLineServer _plc = new();
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "kv", "protocol": "KeyenceUpperLink",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}} } } ]
                }
                """),
            r => r.AddKeyenceUpperLink());
        _hosts.Add(host);
        return host.GetClient("kv");
    }

    [Fact]
    public async Task Word_read_sends_reference_command_and_decodes_values()
    {
        string? captured = null;
        _plc.OnLine = line =>
        {
            captured = line;
            return "00011 65535";
        };

        var client = CreateClient();
        var words = await client.ReadWordsAsync("DM100", 2);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 11, 65535 }, words.Value);
        Assert.Equal("RDS DM100.U 2", captured);
    }

    [Fact]
    public async Task Typed_int_read_uses_cdab_layout()
    {
        // DINT 0x12345678 in DM0/DM1: low word first (0x5678), then high word (0x1234).
        _plc.OnLine = _ => "22136 04660";

        var client = CreateClient();

        Assert.Equal(0x1234_5678, (await client.ReadAsync<int>("DM0")).Value);
    }

    [Fact]
    public async Task Bit_write_sends_manual_example_command()
    {
        string? captured = null;
        _plc.OnLine = line =>
        {
            captured = line;
            return "OK";
        };

        var client = CreateClient();
        var write = await client.WriteBitsAsync("R100", [true, false, true, false]);

        Assert.True(write.Success, write.ToString());
        Assert.Equal("WRS R100 4 1 0 1 0", captured);
    }

    [Fact]
    public async Task Bit_read_unpacks_one_zero_tokens()
    {
        _plc.OnLine = _ => "1 0 1 0";

        var client = CreateClient();
        var bits = await client.ReadBitsAsync("R100", 4);

        Assert.True(bits.Success, bits.ToString());
        Assert.Equal(new[] { true, false, true, false }, bits.Value);
    }

    [Fact]
    public async Task Error_reply_maps_to_DeviceRejected()
    {
        _plc.OnLine = _ => "E0";

        var client = CreateClient();
        var result = await client.ReadWordsAsync("DM999999", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.DeviceRejected, result.Kind);
        Assert.Equal("E0", result.ErrorCode);
    }

    [Fact]
    public async Task Silent_server_times_out_and_closes_the_connection()
    {
        _plc.OnLine = _ => ""; // never answer

        var client = CreateClient(timeoutMs: 300);
        var result = await client.ReadWordsAsync("DM0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Word_device_bit_access_is_rejected()
    {
        var client = CreateClient();

        var bits = await client.ReadBitsAsync("DM100", 1);

        Assert.False(bits.Success);
        Assert.Equal(CommErrorKind.InvalidArgument, bits.Kind);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _plc.DisposeAsync();
    }
}
