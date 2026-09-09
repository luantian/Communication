using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

/// <summary>GE SRTP client against a scripted 56-byte-frame TCP server.</summary>
public sealed class GeSrtpFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedFrameServer _plc = new();
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "ge", "protocol": "GeSrtp",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}} } } ]
                }
                """),
            r => r.AddGeSrtp());
        _hosts.Add(host);
        return host.GetClient("ge");
    }

    private static byte[] AckInline(byte seq, params byte[] data)
    {
        var frame = new byte[56];
        frame[0] = 0x03;
        frame[2] = seq;
        frame[30] = seq;
        frame[31] = 0xD4;
        frame[40] = 0x01;
        frame[41] = 0x01;
        data.CopyTo(frame, 44);
        return frame;
    }

    [Fact]
    public async Task Init_handshake_and_word_write_then_buffered_read()
    {
        byte[]? lastRequest = null;
        _plc.OnFrame = frame =>
        {
            if (frame.All(b => b == 0x00))
            {
                var init = new byte[56];
                init[0] = 0x01; // INIT_ACK
                return init;
            }

            lastRequest = frame;
            var seq = frame[2];
            if (frame[42] == 0x07)
                return AckInline(seq); // write ACK

            // read: 0x94 header with length + follow-up data frame (2 words)
            var header = new byte[56];
            header[0] = 0x03;
            header[2] = seq;
            header[4] = 0x04; // 4 data bytes
            header[30] = seq;
            header[31] = 0x94;
            return [.. header, 0x34, 0x12, 0x78, 0x56];
        };

        var client = CreateClient();

        Assert.True((await client.WriteWordsAsync("R100", [0x1234])).Success);
        Assert.NotNull(lastRequest);
        Assert.Equal(0x07, lastRequest![42]);
        Assert.Equal(0x08, lastRequest[43]);
        Assert.Equal(0x63, lastRequest[44]); // 100-1

        var words = await client.ReadWordsAsync("R100", 2);
        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, words.Value);
    }

    [Fact]
    public async Task Status_error_maps_to_DeviceRejected()
    {
        _plc.OnFrame = frame =>
        {
            if (frame.All(b => b == 0x00))
            {
                var init = new byte[56];
                init[0] = 0x01;
                return init;
            }

            var nack = new byte[56];
            nack[0] = 0x03;
            nack[2] = frame[2];
            nack[30] = frame[2];
            nack[31] = 0xD1;
            nack[42] = 0x01; // illegal service request
            return nack;
        };

        var client = CreateClient();
        var result = await client.ReadWordsAsync("R1", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.DeviceRejected, result.Kind);
        Assert.Equal("0x01/0x00", result.ErrorCode);
    }

    [Fact]
    public async Task Silent_server_times_out_and_closes_the_connection()
    {
        _plc.OnFrame = _ => null;

        var client = CreateClient(timeoutMs: 300);
        var result = await client.ReadWordsAsync("R1", 1);

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
