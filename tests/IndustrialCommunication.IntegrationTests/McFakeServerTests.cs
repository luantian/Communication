using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using IndustrialCommunication.Mitsubishi;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class McFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedMcPlc _plc = new();
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(string frame = "4E", int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "mc", "protocol": "MitsubishiMc",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}}, "frame": "{{frame}}", "monitorTimerMs": 3000 } } ]
                }
                """),
            r => r.AddMitsubishiMc());
        _hosts.Add(host);
        return host.GetClient("mc");
    }

    private static byte[] Ok3E(params byte[] data)
    {
        // D0 00 | route | len | 00 00 | data
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

    private static byte[] Ok4E(byte serLow, byte serHigh, params byte[] data)
    {
        var response = new byte[11 + 2 + data.Length];
        response[0] = 0xD4;
        response[1] = 0x00;
        response[2] = serLow;
        response[3] = serHigh;
        response[4] = 0x00;
        response[5] = 0xFF;
        response[6] = 0xFF;
        response[7] = 0x03;
        response[8] = 0x00;
        response[9] = (byte)((2 + data.Length) & 0xFF);
        response[10] = (byte)((2 + data.Length) >> 8);
        data.CopyTo(response, 13);
        return response;
    }

    [Fact]
    public async Task Word_read_request_matches_golden_bytes_and_decodes_response()
    {
        _plc.OnRequest = request =>
        {
            // Request: read D100, 2 words, 3E frame.
            Assert.Equal(
            [
                0x50, 0x00,
                0x00, 0xFF, 0xFF, 0x03, 0x00,
                0x0C, 0x00,
                0x0C, 0x00,                     // monitor timer 3000 ms = 12 units
                0x01, 0x04, 0x00, 0x00,         // read, word
                0x64, 0x00, 0x00,               // D100
                0xA8,                           // D
                0x02, 0x00,
            ], request);
            return Ok3E(0x34, 0x12, 0x78, 0x56); // words [0x1234, 0x5678]
        };

        var client = CreateClient(frame: "3E");
        var words = await client.ReadWordsAsync("D100", 2);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, words.Value);
    }

    [Fact]
    public async Task Typed_int_read_uses_cdab_layout()
    {
        // A DINT 0x12345678 in D100/D101: low word in D100, high word in D101, each word LE on the wire.
        _plc.OnRequest = _ => Ok3E(0x78, 0x56, 0x34, 0x12);

        var client = CreateClient(frame: "3E");

        Assert.Equal(0x1234_5678, (await client.ReadAsync<int>("D100")).Value);
    }

    [Fact]
    public async Task Bit_write_sends_packed_bits_and_reports_success()
    {
        byte[]? captured = null;
        _plc.OnRequest = request =>
        {
            captured = request;
            return Ok3E();
        };

        var client = CreateClient(frame: "3E");
        var write = await client.WriteBitsAsync("M0", [true, false, true, true]);

        Assert.True(write.Success, write.ToString());
        Assert.NotNull(captured);
        // Command tail: 01 14 01 00 | 00 00 00 | 90 | 04 00 | 10 11
        var tail = captured![(captured!.Length - 12)..];
        Assert.Equal(new byte[] { 0x01, 0x14, 0x01, 0x00, 0x00, 0x00, 0x00, 0x90, 0x04, 0x00, 0x10, 0x11 }, tail);
    }

    [Fact]
    public async Task Bit_read_unpacks_nibbles()
    {
        _plc.OnRequest = _ => Ok3E(0x01, 0x11, 0x10);

        var client = CreateClient(frame: "3E");
        var bits = await client.ReadBitsAsync("M0", 6);

        Assert.True(bits.Success, bits.ToString());
        Assert.Equal(new[] { false, true, true, true, true, false }, bits.Value);
    }

    [Fact]
    public async Task Non_zero_end_code_maps_to_DeviceRejected()
    {
        _plc.OnRequest = _ =>
        {
            var response = Ok3E();
            response[9] = 0x01;  // end code low byte → 0xC001
            response[10] = 0xC0;
            return response;
        };

        var client = CreateClient(frame: "3E");
        var result = await client.ReadWordsAsync("D0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.DeviceRejected, result.Kind);
        Assert.Equal("0xC001", result.ErrorCode);
    }

    [Fact]
    public async Task FourE_roundtrip_and_serial_check()
    {
        _plc.OnRequest = request =>
        {
            Assert.Equal(0x54, request[0]);
            var serialLow = request[2];
            var serialHigh = request[3];
            return Ok4E(serialLow, serialHigh, 0x39, 0x05); // word 0x0539 = 1337
        };

        var client = CreateClient(frame: "4E");
        var word = await client.ReadWordsAsync("W10", 1);

        Assert.True(word.Success, word.ToString());
        Assert.Equal((ushort)1337, word.Value[0]);
    }

    [Fact]
    public async Task FourE_serial_mismatch_is_a_protocol_error()
    {
        _plc.OnRequest = _ => Ok4E(0xDE, 0xAD, 0x39, 0x05); // wrong serial

        var client = CreateClient(frame: "4E");
        var word = await client.ReadWordsAsync("W10", 1);

        Assert.False(word.Success);
        Assert.Equal(CommErrorKind.ProtocolError, word.Kind);
    }

    [Fact]
    public async Task Word_device_bit_access_is_rejected()
    {
        var client = CreateClient(frame: "3E");

        var bits = await client.ReadBitsAsync("D100", 1);

        Assert.False(bits.Success);
        Assert.Equal(CommErrorKind.InvalidArgument, bits.Kind);
    }

    [Fact]
    public async Task Silent_server_times_out_and_closes_the_connection()
    {
        _plc.OnRequest = _ => []; // never answer

        var client = CreateClient(frame: "3E", timeoutMs: 300);
        var result = await client.ReadWordsAsync("D0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
        Assert.False(client.IsConnected);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _plc.DisposeAsync();
    }
}
