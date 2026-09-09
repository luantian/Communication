using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using IndustrialCommunication.Panasonic;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class MewtocolFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedLineServer _plc = new(strictCrLf: false);
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "fp", "protocol": "PanasonicMewtocol",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}}, "station": 1 } } ]
                }
                """),
            r => r.AddPanasonicMewtocol());
        _hosts.Add(host);
        return host.GetClient("fp");
    }

    private static string WithBcc(string body) =>
        body + Bcc(body);

    private static string Bcc(string body)
    {
        byte acc = 0;
        foreach (var b in System.Text.Encoding.ASCII.GetBytes(body))
            acc ^= b;
        return acc.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Word_read_sends_manual_frame_and_decodes_words()
    {
        string? captured = null;
        _plc.OnLine = line =>
        {
            captured = line;
            return WithBcc("%01$RD630044330A00");
        };

        var client = CreateClient();
        var words = await client.ReadWordsAsync("DT1105", 3);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x0063, 0x3344, 0x000A }, words.Value);
        Assert.Equal("%01#RDD011050110757", captured);
    }

    [Fact]
    public async Task Typed_int_read_uses_cdab_layout()
    {
        // 32-bit 0x12345678: low word 0x5678 first ("7856"), high word 0x1234 ("3412")
        _plc.OnLine = _ => WithBcc("%01$RD78563412");

        var client = CreateClient();

        Assert.Equal(0x1234_5678, (await client.ReadAsync<int>("DT0")).Value);
    }

    [Fact]
    public async Task Bit_read_uses_rcs_form()
    {
        string? captured = null;
        _plc.OnLine = line =>
        {
            captured = line;
            return WithBcc("%01$RC1");
        };

        var client = CreateClient();
        var bit = await client.ReadBitsAsync("X000A", 1);

        Assert.True(bit.Success, bit.ToString());
        Assert.Equal([true], bit.Value);
        Assert.Equal("%01#RCSX000A6C", captured);
    }

    [Fact]
    public async Task Bit_write_sends_manual_frame()
    {
        string? captured = null;
        _plc.OnLine = line =>
        {
            captured = line;
            return WithBcc("%01$WC");
        };

        var client = CreateClient();
        var write = await client.WriteBitsAsync("Y000A", [true]);

        Assert.True(write.Success, write.ToString());
        Assert.Equal("%01#WCSY000A159", captured);
    }

    [Fact]
    public async Task Error_reply_maps_to_DeviceRejected()
    {
        _plc.OnLine = _ => WithBcc("%01!61");

        var client = CreateClient();
        var result = await client.ReadWordsAsync("DT99999", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.DeviceRejected, result.Kind);
        Assert.Equal("61", result.ErrorCode);
    }

    [Fact]
    public async Task Silent_server_times_out_and_closes_the_connection()
    {
        _plc.OnLine = _ => "";

        var client = CreateClient(timeoutMs: 300);
        var result = await client.ReadWordsAsync("DT0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Word_device_bit_access_is_rejected()
    {
        var client = CreateClient();

        var bits = await client.ReadBitsAsync("DT100", 1);

        Assert.False(bits.Success);
        Assert.Equal(CommErrorKind.InvalidArgument, bits.Kind);
    }

    [Fact]
    public async Task Multi_frame_read_reassembles_segments()
    {
        var continuationRequests = 0;
        // 30 words (120 hex chars) split into two segments: 20 words then 10 words.
        var first = Enumerable.Range(0, 20).Select(i => MewtocolFrame.WordsHex((ushort)(i + 1))).ToList();
        var second = Enumerable.Range(20, 10).Select(i => MewtocolFrame.WordsHex((ushort)(i + 1))).ToList();

        _plc.OnLine = line =>
        {
            if (line.EndsWith("**&", StringComparison.Ordinal))
            {
                continuationRequests++;
                return WithBcc("%01$RD" + string.Concat(second));
            }

            return WithBcc("%01$RD" + string.Concat(first) + "&");
        };

        var client = CreateClient();
        var words = await client.ReadWordsAsync("DT0", 30);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(Enumerable.Range(1, 30).Select(i => (ushort)i).ToArray(), words.Value);
        Assert.Equal(1, continuationRequests);
    }

    [Fact]
    public async Task Heartbeat_probe_succeeds()
    {
        _plc.OnLine = _ => WithBcc("%01$RT4615328000000000");

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
