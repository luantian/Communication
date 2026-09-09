using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class EipFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedEipPlc _plc = new();
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "ab", "protocol": "RockwellEtherNetIp",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}}, "slot": 0 } } ]
                }
                """),
            r => r.AddRockwellEtherNetIp());
        _hosts.Add(host);
        return host.GetClient("ab");
    }

    [Fact]
    public async Task Register_and_typed_dint_read()
    {
        var client = CreateClient();

        var value = await client.ReadAsync<int>("MyDint");

        Assert.True(value.Success, value.ToString());
        Assert.Equal(0x1234_5678, value.Value);
    }

    [Fact]
    public async Task Typed_bool_and_int_reads_use_native_types()
    {
        var client = CreateClient();

        Assert.True((await client.ReadAsync<bool>("MyBool")).Value);
        Assert.Equal((short)0x1234, (await client.ReadAsync<short>("MyInt")).Value);
    }

    [Fact]
    public async Task Word_read_decodes_le_words_from_dint()
    {
        var client = CreateClient();

        var words = await client.ReadWordsAsync("MyDint", 2);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x5678, 0x1234 }, words.Value);
    }

    [Fact]
    public async Task Bit_of_dint_read_extracts_locally()
    {
        var client = CreateClient();

        // 0x12345678 bit 3 = 0 (byte 0x78 → 0111 1000, bit 3 = 1)
        Assert.True((await client.ReadAsync<bool>("MyDint.3")).Value);
        Assert.False((await client.ReadAsync<bool>("MyDint.0")).Value);
    }

    [Fact]
    public async Task Typed_write_roundtrip_command()
    {
        byte[]? lastFrame = null;
        _plc.ReceivedFrames.Clear();
        var client = CreateClient();

        var write = await client.WriteAsync<int>("MyDint", -1);
        Assert.True(write.Success, write.ToString());

        lock (_plc.ReceivedFrames)
        {
            lastFrame = _plc.ReceivedFrames.LastOrDefault(f => f.Length > 50 && f[50] == 0x4D);
        }
        Assert.NotNull(lastFrame);
        // Embedded CIP Write Tag at offset 50: 4D SS <IOI> type(2) count(2) data
        Assert.Equal(0x4D, lastFrame![50]);
        var ioiEnd = 52 + (lastFrame[51] * 2);
        Assert.Equal(0xC4, lastFrame[ioiEnd]);
        Assert.Equal(1, lastFrame[ioiEnd + 2]);
        Assert.Equal(0xFF, lastFrame[ioiEnd + 4]); // -1 LE
    }

    [Fact]
    public async Task Cip_path_error_maps_to_InvalidAddress()
    {
        _plc.ForceCipStatus = 0x04;
        var client = CreateClient();

        var result = await client.ReadAsync<int>("NoSuchTag");

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.InvalidAddress, result.Kind);
        Assert.Equal("0x04", result.ErrorCode);
    }

    [Fact]
    public async Task Silent_server_times_out_and_closes_the_connection()
    {
        _plc.Silent = true;
        var client = CreateClient(timeoutMs: 300);

        var result = await client.ReadAsync<int>("MyDint");

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Fragmented_read_assembles_chunks()
    {
        var client = CreateClient();

        var words = await client.ReadWordsAsync("BigArray", 500);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(ScriptedEipPlc.BigArrayContents, words.Value);
    }

    [Fact]
    public async Task Udt_read_returns_raw_structure_bytes()
    {
        var client = CreateClient();

        var raw = await ((IndustrialCommunication.Rockwell.EipPlcClient)client).ReadTagRawAsync("MyUdt");

        Assert.Equal(ScriptedEipPlc.StructureContents, raw);
    }

    [Fact]
    public async Task Udt_word_read_assembles_the_whole_structure()
    {
        var client = CreateClient();

        // 350 words = the whole 700-byte structure; the plain read only returns 2 words,
        // so the driver must fall back to fragmented reads transparently.
        var words = await client.ReadWordsAsync("MyUdt", 350);

        Assert.True(words.Success, words.ToString());
        // words are little-endian pairs of the raw structure bytes
        var expected = new ushort[350];
        for (int i = 0; i < 700; i += 2)
            expected[i / 2] = (ushort)((ScriptedEipPlc.StructureContents[i + 1] << 8) | ScriptedEipPlc.StructureContents[i]);
        Assert.Equal(expected, words.Value);
    }

    [Fact]
    public async Task Heartbeat_probe_succeeds()
    {
        var client = CreateClient();

        var heartbeat = await client.HeartbeatAsync();

        Assert.True(heartbeat.Success, heartbeat.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _plc.DisposeAsync();
    }
}
