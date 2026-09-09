using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

/// <summary>
/// LS FEnet client against a length-prefixed scripted TCP server. The server reads the 20-byte
/// header plus the instruction length, then answers through a test responder.
/// </summary>
public sealed class LsFEnetFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedLengthPrefixedServer _plc = new(headerLength: 20, lengthAt: 16);
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "ls", "protocol": "LsFEnet",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}} } } ]
                }
                """),
            r => r.AddLsFEnet());
        _hosts.Add(host);
        return host.GetClient("ls");
    }

    private static byte[] ReadReply(byte[] request, params byte[] data)
    {
        var reply = new byte[32 + data.Length];
        request.AsSpan(0, 20).CopyTo(reply);                // header (instruction is rebuilt below)
        reply[13] = 0x11;                                   // PLC → PC
        reply[20] = 0x55;                                   // read response
        reply[22] = 0x02;                                   // individual WORD type
        reply[26] = 0x00;
        reply[27] = 0x00;                                   // error OK
        reply[28] = 0x01;
        reply[29] = 0x00;                                   // variable count
        reply[30] = (byte)data.Length;                      // data byte count
        reply[31] = 0x00;
        data.CopyTo(reply, 32);

        // instruction length = 12 fixed + data
        reply[16] = (byte)(12 + data.Length);
        reply[17] = 0x00;
        reply[19] = Bcc(reply);
        return reply;
    }

    private static byte[] WriteAck(byte[] request)
    {
        var reply = new byte[28];
        request.AsSpan(0, 20).CopyTo(reply);
        reply[13] = 0x11;
        reply[20] = 0x59;                                   // write response
        reply[22] = 0x02;                                   // type
        reply[26] = 0x00;
        reply[27] = 0x00;                                   // error OK
        reply[16] = 0x08;                                   // instruction length = 8
        reply[17] = 0x00;
        reply[19] = Bcc(reply);
        return reply;
    }

    private static byte Bcc(byte[] frame)
    {
        byte acc = 0;
        for (int i = 0; i < 19; i++)
            acc += frame[i];
        return acc;
    }

    [Fact]
    public async Task Word_write_then_read_roundtrip()
    {
        byte[]? lastRequest = null;
        _plc.OnFrame = frame =>
        {
            lastRequest = frame;
            return frame[20] == 0x58 ? WriteAck(frame) : ReadReply(frame, 0x34, 0x12, 0x78, 0x56);
        };

        var client = CreateClient();

        var write = await client.WriteWordsAsync("D100", [0x1234, 0x5678]);
        Assert.True(write.Success, write.ToString());
        Assert.NotNull(lastRequest);
        Assert.Equal(0x58, lastRequest![20]);
        Assert.Contains((byte)'%', lastRequest);

        var words = await client.ReadWordsAsync("D100", 2);
        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, words.Value);
    }

    [Fact]
    public async Task Bit_write_uses_individual_mode()
    {
        byte[]? lastRequest = null;
        _plc.OnFrame = frame =>
        {
            lastRequest = frame;
            return frame[20] == 0x58 ? WriteAck(frame) : ReadReply(frame, 0x01);
        };

        var client = CreateClient();
        var write = await client.WriteBitsAsync("M100.3", [true]);

        Assert.True(write.Success, write.ToString());
        Assert.Equal(0x58, lastRequest![20]);
        Assert.Equal(0x00, lastRequest[22]);       // individual BIT type
        Assert.Equal(0x01, lastRequest[26]);       // 1 variable (after cmd, type, reserved)
        // trailing data: length (2 bytes, 01 00) + value 01
        Assert.Equal(0x01, lastRequest[^3]);
        Assert.Equal(0x00, lastRequest[^2]);
        Assert.Equal(0x01, lastRequest[^1]);
    }

    [Fact]
    public async Task Error_status_maps_to_DeviceRejected()
    {
        _plc.OnFrame = frame =>
        {
            var reply = ReadReply(frame);
            reply[26] = 0x04;                       // outside device range (LE)
            return reply;
        };

        var client = CreateClient();
        var result = await client.ReadWordsAsync("D0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.DeviceRejected, result.Kind);
        Assert.Equal("0x0004", result.ErrorCode);
    }

    [Fact]
    public async Task Silent_server_times_out()
    {
        _plc.OnFrame = _ => null;

        var client = CreateClient(timeoutMs: 300);
        var result = await client.ReadWordsAsync("D0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _plc.DisposeAsync();
    }
}
