using System.Net.Sockets;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.Mitsubishi;

/// <summary>Mitsubishi MELSEC MC-protocol client (3E/4E binary frames over TCP), implemented in-library.</summary>
public sealed class McProtocolClient : McClientBase
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public McProtocolClient(McOptions options, DeviceConfig device, ClientBuildContext context)
        : base(options, device, context)
    {
    }

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(Options.Ip, Options.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
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

    protected override async Task<ReadOnlyMemory<byte>> TransactAsync(byte[] request, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var frame = Options.ResolveFrame();
        int lengthOffset = 2 + (frame == McFrameKind.Frame4E ? 2 : 0) + 5;
        int headerLength = lengthOffset + 2;
        var response = new byte[headerLength + 64];
        var received = await ReadExactlyAsync(stream, response.AsMemory(0, headerLength), ct).ConfigureAwait(false);

        var payloadLength = response[lengthOffset] | (response[lengthOffset + 1] << 8);
        var total = lengthOffset + 2 + payloadLength;
        if (total > response.Length)
        {
            var grown = new byte[total];
            response.AsSpan(0, received).CopyTo(grown);
            response = grown;
        }

        await ReadExactlyAsync(stream, response.AsMemory(received, total - received), ct).ConfigureAwait(false);
        return response.AsMemory(0, total);
    }

    private static async Task<int> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"The PLC closed the connection after {total} of {buffer.Length} expected bytes.");
            total += read;
        }
        return total;
    }
}
