using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>
/// Scripted FINS/TCP server: performs the node-address handshake, then answers FINS data frames
/// through a test-provided responder. Captures every received FINS frame.
/// </summary>
public sealed class ScriptedFinsPlc : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public byte ServerNode { get; set; } = 0x0A;

    /// <summary>Receives the FINS frame (without the TCP wrapper); returns the FINS response frame (also without wrapper).</summary>
    public Func<byte[], byte[]> OnFinsRequest { get; set; } = static _ => [];

    public List<byte[]> ReceivedFrames { get; } = [];

    public int Port { get; }

    public ScriptedFinsPlc()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            using (client)
            {
                try
                {
                    await HandleConnectionAsync(client, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();

        // Node address handshake: 20 bytes in, 24 bytes out.
        var handshake = new byte[20];
        if (await ReadExactlyAsync(stream, handshake, ct).ConfigureAwait(false) == 0)
            return;

        var clientNode = handshake[19];
        var reply = new byte[24];
        "FINS"u8.CopyTo(reply.AsSpan(0));
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), 16);
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(8), 0x00000001); // node address data reply
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(12), 0);
        reply[19] = clientNode; // echo/assign the client node
        reply[23] = ServerNode;
        await stream.WriteAsync(reply, ct).ConfigureAwait(false);

        var header = new byte[8];
        while (await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false) > 0)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            var frame = new byte[length];
            await ReadExactlyAsync(stream, frame, ct).ConfigureAwait(false);

            lock (ReceivedFrames)
            {
                ReceivedFrames.Add(frame);
            }

            var response = OnFinsRequest(frame);
            if (response.Length == 0)
                continue;

            var packet = new byte[8 + response.Length];
            "FINS"u8.CopyTo(packet.AsSpan(0));
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), response.Length);
            response.CopyTo(packet, 8);
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds a normal FINS response frame for a request: mirrors the header, sets ICF 0xC0, adds the response code and data.</summary>
    public static byte[] ResponseFor(ReadOnlySpan<byte> request, ushort responseCode, ReadOnlySpan<byte> data)
    {
        var response = new byte[12 + data.Length];
        request[..10].CopyTo(response.AsSpan(0));
        response[0] = 0xC0;
        // mirror the routing: DNA(3)↔SNA(6), DA1(4)↔SA1(7), DA2(5)↔SA2(8)
        (response[3], response[6]) = (response[6], response[3]);
        (response[4], response[7]) = (response[7], response[4]);
        (response[5], response[8]) = (response[8], response[5]);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), responseCode);
        data.CopyTo(response.AsSpan(12));
        return response;
    }

    private static async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                return 0;
            total += read;
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
