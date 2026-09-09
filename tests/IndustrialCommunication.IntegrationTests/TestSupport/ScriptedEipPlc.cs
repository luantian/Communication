using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>
/// Scripted EtherNet/IP controller: performs RegisterSession, then answers SendRRData requests.
/// The response is built from the requested tag name found in the CIP request (MyDint/MyBool/MyInt).
/// </summary>
public sealed class ScriptedEipPlc : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public const uint SessionHandle = 0x1122_3344;

    /// <summary>When set, read/write replies carry this CIP status instead of 0.</summary>
    public byte ForceCipStatus { get; set; }

    /// <summary>When true, requests are never answered.</summary>
    public bool Silent { get; set; }

    public List<byte[]> ReceivedFrames { get; } = [];

    public int Port { get; }

    public ScriptedEipPlc()
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
        var header = new byte[24];

        while (await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
        {
            var command = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan());
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            var body = new byte[length];
            if (length > 0)
                await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false);

            var request = new byte[24 + length];
            header.CopyTo(request, 0);
            body.CopyTo(request, 24);
            lock (ReceivedFrames)
            {
                ReceivedFrames.Add(request);
            }

            if (Silent)
                continue;

            byte[]? reply = command switch
            {
                0x0065 => BuildRegisterReply(request),
                0x006F => BuildSendRRReply(request),
                _ => null,
            };
            if (reply is not null)
                await stream.WriteAsync(reply, ct).ConfigureAwait(false);
        }
    }

    private static byte[] BuildRegisterReply(byte[] request)
    {
        var reply = (byte[])request.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), SessionHandle);
        return reply;
    }

    /// <summary>Content served for reads of the "BigArray" tag: 500 INT elements, value = index.</summary>
    public static ushort[] BigArrayContents => Enumerable.Range(0, 500).Select(i => (ushort)i).ToArray();

    /// <summary>Raw bytes served for the "MyUdt" structure tag (700 bytes → two fragmented chunks).</summary>
    public static byte[] StructureContents => Enumerable.Range(0, 700).Select(i => (byte)(i % 251)).ToArray();

    private byte[] BuildSendRRReply(byte[] request)
    {
        var requestText = Encoding.ASCII.GetString(request);
        // UDI at 40 is the Unconnected Send wrapper; the embedded CIP service sits at 50.
        var service = request.Length > 50 ? request[50] : request[40];

        // Fragmented read (0x52) on BigArray: serve 300-byte chunks, 0x06 while more remains.
        if (service == 0x52 && requestText.Contains("BigArray"))
        {
            var pathWords = request[51];
            var count = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(52 + pathWords * 2));
            var byteOffset = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(54 + pathWords * 2));
            var contents = BigArrayContents;
            var total = Math.Min(count * 2, contents.Length * 2);
            var take = (int)Math.Min(300, total - byteOffset);
            var status = byteOffset + take < total ? (byte)0x06 : (byte)0x00;

            var udi = new byte[4 + 2 + take];
            udi[0] = 0xD2;
            udi[2] = status;
            BinaryPrimitives.WriteUInt16LittleEndian(udi.AsSpan(4), 0xC3); // INT
            for (int i = 0; i < take; i += 2)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    udi.AsSpan(6 + i), contents[(int)(byteOffset / 2) + i / 2]);
            return WrapReply(request, udi);
        }

        // UDT reads on "MyUdt": plain read returns a structure type + a small slice;
        // fragmented reads serve the full raw structure in 300-byte chunks.
        if (requestText.Contains("MyUdt"))
        {
            if (service == 0x52)
            {
                var pathWords = request[51];
                var byteOffset = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(54 + pathWords * 2));
                var structure = StructureContents;
                var take = (int)Math.Min(300, structure.Length - byteOffset);
                var status = byteOffset + take < structure.Length ? (byte)0x06 : (byte)0x00;

                var udi = new byte[4 + 4 + take];
                udi[0] = 0xD2;
                udi[2] = status;
                udi[4] = 0xA0; // structure marker
                udi[5] = 0x02; // fixed second byte of the type field
                udi[6] = 0xCE; // structure handle 0x0FCE
                udi[7] = 0x0F;
                structure.AsSpan((int)byteOffset, take).CopyTo(udi.AsSpan(8));
                return WrapReply(request, udi);
            }

            // plain 0x4C read: structure header + only the first 4 member bytes
            return WrapReply(request, [0xCC, 0x00, 0x00, 0x00, 0xA0, 0x02, 0xCE, 0x0F, 0x78, 0x56, 0x34, 0x12]);
        }

        byte[] simpleUdi = service switch
        {
            0x4D when ForceCipStatus == 0 => [0xCD, 0x00, 0x00, 0x00], // write reply: no data
            0x01 when ForceCipStatus == 0 => [0x81, 0x00, 0x00, 0x00], // GetAttributesAll reply (heartbeat)
            _ when ForceCipStatus != 0 => [unchecked((byte)(service | 0x80)), 0x00, ForceCipStatus, 0x00],
            _ when requestText.Contains("MyBool") => [0xCC, 0x00, 0x00, 0x00, 0xC1, 0x00, 0x01],
            _ when requestText.Contains("MyInt") => [0xCC, 0x00, 0x00, 0x00, 0xC3, 0x00, 0x34, 0x12],
            _ => [0xCC, 0x00, 0x00, 0x00, 0xC4, 0x00, 0x78, 0x56, 0x34, 0x12], // MyDint = 0x12345678
        };

        return WrapReply(request, simpleUdi);
    }

    private static byte[] WrapReply(byte[] request, byte[] udi)
    {
        var body = new byte[16 + udi.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1);       // timeout
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 2);       // item count
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), 0x00B2); // unconnected data item
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)udi.Length);
        udi.CopyTo(body, 16);

        var reply = new byte[24 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(0), 0x006F);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2), (ushort)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), SessionHandle);
        request.AsSpan(12, 8).CopyTo(reply.AsSpan(12)); // echo sender context
        body.CopyTo(reply, 24);
        return reply;
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
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
