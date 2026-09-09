using System.Buffers.Binary;
using System.Net.Sockets;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Omron;

/// <summary>Omron FINS/TCP client (CP/CJ/CJ2/NJ series): node-address handshake plus FINS frames in TCP packets.</summary>
public sealed class FinsTcpClient : FinsClientBase
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public FinsTcpClient(FinsOptions options, DeviceConfig device, ClientBuildContext context)
        : base(options, device, context)
    {
    }

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        NetworkStream? stream = null;
        try
        {
            await tcp.ConnectAsync(Options.Ip, Options.Port, ct).ConfigureAwait(false);
            stream = tcp.GetStream();

            // FINS/TCP node address handshake before any FINS frames.
            await stream.WriteAsync(FinsFrame.BuildNodeHandshake(Options.SourceNode), ct).ConfigureAwait(false);
            var handshake = new byte[24];
            await ReadExactlyAsync(stream, handshake, ct).ConfigureAwait(false);
            var (assignedLocalNode, serverNode) = FinsFrame.ParseNodeHandshakeResponse(handshake);

            // The PLC's answer is authoritative: it tells us which node we got and which node it is.
            if (Options.SourceNode == 0 || assignedLocalNode != 0)
                SourceNode = assignedLocalNode;
            if (!Options.DestNode.HasValue && serverNode != 0)
                DestNode = serverNode;
        }
        catch
        {
            stream?.Dispose();
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = stream;
    }

    protected override Task DoDisconnectAsync()
    {
        var stream = _stream;
        var tcp = _tcp;
        _stream = null;
        _tcp = null;

        stream?.Dispose();
        tcp?.Dispose();
        return Task.CompletedTask;
    }

    protected override async Task<ReadOnlyMemory<byte>> TransactAsync(ReadOnlyMemory<byte> finsFrame, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");
        await stream.WriteAsync(FinsFrame.WrapFinsFrame(finsFrame.Span), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[8];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        if (length is < 2 or > 1_000_000)
            throw new InvalidDataException($"FINS/TCP frame length {length} is out of range.");

        var body = new byte[length];
        await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false);

        var full = new byte[8 + length];
        header.CopyTo(full, 0);
        body.CopyTo(full, 8);
        return FinsFrame.UnwrapFinsFrame(full);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"The PLC closed the connection after {total} of {buffer.Length} expected bytes.");
            total += read;
        }
    }
}

/// <summary>
/// Omron FINS/UDP client: bare FINS frames as datagrams, no node handshake. Configure
/// sourceNode/destNode explicitly (or leave destNode to default to the PLC IP's last byte).
/// </summary>
public sealed class FinsUdpClient : FinsClientBase
{
    private UdpClient? _udp;

    public FinsUdpClient(FinsOptions options, DeviceConfig device, ClientBuildContext context)
        : base(options, device, context)
    {
    }

    protected override Task DoConnectAsync(CancellationToken ct)
    {
        var udp = new UdpClient();
        try
        {
            udp.Connect(Options.Ip, Options.Port);
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            udp.Dispose();
            throw;
        }

        _udp = udp;
        return Task.CompletedTask;
    }

    protected override Task DoDisconnectAsync()
    {
        var udp = _udp;
        _udp = null;
        udp?.Dispose();
        return Task.CompletedTask;
    }

    protected override async Task<ReadOnlyMemory<byte>> TransactAsync(ReadOnlyMemory<byte> finsFrame, CancellationToken ct)
    {
        var udp = _udp ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");

        _ = await udp.SendAsync(finsFrame, ct).ConfigureAwait(false);
        var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        return result.Buffer;
    }
}
