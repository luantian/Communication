using System.Buffers.Binary;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using IndustrialCommunication.Omron;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class FinsFakeServerTests : IAsyncDisposable
{
    private readonly ScriptedFinsPlc _plc = new();
    private readonly List<CommunicationHost> _hosts = [];

    private IPlcClient CreateClient(int timeoutMs = 1000)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": {{timeoutMs}}, "connectRetries": 0 },
                  "devices": [ { "name": "fins", "protocol": "OmronFins",
                                 "connection": { "ip": "127.0.0.1", "port": {{_plc.Port}},
                                                 "sourceNode": 25, "destNode": 10 } } ]
                }
                """),
            r => r.AddOmronFins());
        _hosts.Add(host);
        return host.GetClient("fins");
    }

    [Fact]
    public async Task Word_read_request_matches_golden_bytes_and_decodes_be_words()
    {
        byte[]? captured = null;
        _plc.OnFinsRequest = frame =>
        {
            captured = frame;
            return ScriptedFinsPlc.ResponseFor(frame, 0, [0x12, 0x34, 0x56, 0x78]);
        };

        var client = CreateClient();
        var words = await client.ReadWordsAsync("DM100", 2);

        Assert.True(words.Success, words.ToString());
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, words.Value);

        Assert.NotNull(captured);
        // FINS header (10) + command 0101 + area(2) + word(2) + bit(1) + count(2) = 19
        Assert.Equal(19, captured!.Length);
        Assert.Equal(0x80, captured[0]);
        Assert.Equal(0x0A, captured[4]);       // DA1 = PLC node 10
        Assert.Equal(0x19, captured[7]);       // SA1 = client node 25
        Assert.Equal(0x0101, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(10)));
        Assert.Equal(0x0082, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(12)));
        Assert.Equal(100, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(14)));
        Assert.Equal(0, captured[16]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(17)));
    }

    [Fact]
    public async Task Typed_int_read_uses_abcd_layout()
    {
        _plc.OnFinsRequest = frame =>
            ScriptedFinsPlc.ResponseFor(frame, 0, [0x12, 0x34, 0x56, 0x78]);

        var client = CreateClient();

        Assert.Equal(0x1234_5678, (await client.ReadAsync<int>("DM100")).Value);
    }

    [Fact]
    public async Task Bit_write_sends_one_byte_per_bit()
    {
        byte[]? captured = null;
        _plc.OnFinsRequest = frame =>
        {
            captured = frame;
            return ScriptedFinsPlc.ResponseFor(frame, 0, []);
        };

        var client = CreateClient();
        var write = await client.WriteBitsAsync("CIO100.3", [true, false, true, true]);

        Assert.True(write.Success, write.ToString());
        Assert.NotNull(captured);
        Assert.Equal(0x0102, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(10)));
        Assert.Equal(0x0030, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(12))); // CIO bit area
        Assert.Equal(3, captured[16]);       // bit offset
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(17)));
        Assert.Equal(new byte[] { 1, 0, 1, 1 }, captured[19..]);
    }

    [Fact]
    public async Task Bit_read_returns_one_byte_per_bit()
    {
        _plc.OnFinsRequest = frame =>
            ScriptedFinsPlc.ResponseFor(frame, 0, [1, 0, 1]);

        var client = CreateClient();
        var bits = await client.ReadBitsAsync("W10.2", 3);

        Assert.True(bits.Success, bits.ToString());
        Assert.Equal(new[] { true, false, true }, bits.Value);
    }

    [Fact]
    public async Task Non_zero_response_code_maps_to_DeviceRejected()
    {
        _plc.OnFinsRequest = frame =>
            ScriptedFinsPlc.ResponseFor(frame, 0x0104, []); // address out of range

        var client = CreateClient();
        var result = await client.ReadWordsAsync("DM0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.DeviceRejected, result.Kind);
        Assert.Equal("0x0104", result.ErrorCode);
    }

    [Fact]
    public async Task Word_form_used_for_bit_access_is_rejected()
    {
        var client = CreateClient();

        var bits = await client.ReadBitsAsync("DM100", 1);

        Assert.False(bits.Success);
        Assert.Equal(CommErrorKind.InvalidArgument, bits.Kind);
    }

    [Fact]
    public async Task Silent_server_times_out_and_closes_the_connection()
    {
        _plc.OnFinsRequest = _ => []; // never answer

        var client = CreateClient(timeoutMs: 300);
        var result = await client.ReadWordsAsync("DM0", 1);

        Assert.False(result.Success);
        Assert.Equal(CommErrorKind.Timeout, result.Kind);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Handshake_rejection_fails_the_connect()
    {
        // Rewrite the scripted server behavior: reply with error code 2.
        // This is done by starting a raw listener inline.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var rejectTask = Task.Run(async () =>
        {
            using var sock = await listener.AcceptTcpClientAsync();
            var stream = sock.GetStream();
            var handshake = new byte[20];
            await stream.ReadAsync(handshake.AsMemory());
            var reply = new byte[24];
            "FINS"u8.CopyTo(reply.AsSpan(0));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(8), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(12), 2); // error → reject
            await stream.WriteAsync(reply);
        });

        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                { "defaults": { "connectRetries": 0, "connectTimeoutMs": 1500 },
                  "devices": [ { "name": "fins", "protocol": "OmronFins",
                                 "connection": { "ip": "127.0.0.1", "port": {{port}} } } ] }
                """),
            r => r.AddOmronFins());

        await using (host)
        {
            var client = host.GetClient("fins");
            var read = await client.ReadWordsAsync("DM0", 1);

            Assert.False(read.Success);
            Assert.Equal(CommErrorKind.ProtocolError, read.Kind);
        }

        await rejectTask;
        listener.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _plc.DisposeAsync();
    }
}
