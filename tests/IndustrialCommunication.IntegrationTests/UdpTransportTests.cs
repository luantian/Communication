using System.Buffers.Binary;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

/// <summary>UDP transport variants of the hand-written drivers (MC / FINS / Keyence) against a scripted datagram server.</summary>
public sealed class UdpTransportTests : IAsyncDisposable
{
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(string protocol, int port, string extra = "", int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "dev", "protocol": "{{protocol}}",
                                 "connection": { "ip": "127.0.0.1", "port": {{port}}{{extra}} } } ]
                }
                """),
            r =>
            {
                r.AddMitsubishiMcUdp();
                r.AddOmronFinsUdp();
                r.AddKeyenceUpperLinkUdp();
            });
        _hosts.Add(host);
        return host.GetClient("dev");
    }

    private static byte[] Mc3EResponse(params byte[] data)
    {
        var response = new byte[9 + 2 + data.Length];
        response[0] = 0xD0;
        response[1] = 0x00;
        response[2] = 0x00;
        response[3] = 0xFF;
        response[4] = 0xFF;
        response[5] = 0x03;
        response[6] = 0x00;
        response[7] = (byte)((2 + data.Length) & 0xFF);
        response[8] = (byte)((2 + data.Length) >> 8);
        data.CopyTo(response, 11);
        return response;
    }

    [Fact]
    public async Task Mc_udp_word_read()
    {
        await using var plc = new ScriptedUdpPlc();
        plc.OnDatagram = datagram =>
        {
            // request: 3E header(2+5) len(2) mon(2) cmd(2) sub(2) addr(3) code(1) count(2) = 21 bytes
            Assert.Equal(21, datagram.Length);
            Assert.Equal(0x50, datagram[0]);
            return Mc3EResponse(0x34, 0x12);
        };

        var client = CreateClient("MitsubishiMcUdp", plc.Port, extra: ", \"frame\": \"3E\"");
        var words = await client.ReadWordsAsync("D100", 1);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234 }, words.Value);
    }

    [Fact]
    public async Task Fins_udp_word_read()
    {
        await using var plc = new ScriptedUdpPlc();
        plc.OnDatagram = datagram =>
        {
            // bare FINS frame: ICF RSV GCT DNA DA1 DA2 SNA SA1 SA2 SID cmd(2) area(2) addr(3+1) count(2)
            Assert.Equal(19, datagram.Length);
            Assert.Equal(0x80, datagram[0]);
            Assert.Equal(0x0101, BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(10)));
            return ScriptedFinsPlc.ResponseFor(datagram, 0, [0x12, 0x34]);
        };

        var client = CreateClient("OmronFinsUdp", plc.Port, extra: ", \"sourceNode\": 25, \"destNode\": 10");
        var words = await client.ReadWordsAsync("DM100", 1);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234 }, words.Value);
    }

    [Fact]
    public async Task Keyence_udp_word_read()
    {
        await using var plc = new ScriptedUdpPlc();
        plc.OnDatagram = datagram =>
        {
            var text = System.Text.Encoding.ASCII.GetString(datagram).TrimEnd('\r', '\n');
            Assert.Equal("RDS DM100.U 2", text);
            return System.Text.Encoding.ASCII.GetBytes("00011 65535\r\n");
        };

        var client = CreateClient("KeyenceUpperLinkUdp", plc.Port);
        var words = await client.ReadWordsAsync("DM100", 2);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 11, 65535 }, words.Value);
    }

    [Fact]
    public async Task Silent_udp_server_times_out()
    {
        await using var plc = new ScriptedUdpPlc();
        plc.OnDatagram = _ => [];

        var client = CreateClient("MitsubishiMcUdp", plc.Port, extra: ", \"frame\": \"3E\"", timeoutMs: 300);
        var result = await client.ReadWordsAsync("D0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
        Assert.False(client.IsConnected);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
    }
}
