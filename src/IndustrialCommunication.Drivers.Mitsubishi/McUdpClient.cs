using System.Net.Sockets;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Mitsubishi;

/// <summary>
/// Mitsubishi MELSEC MC-protocol client over UDP: identical 3E/4E frames with one request and one
/// response datagram per operation. 4E frames are recommended (the serial number guards against
/// stray datagrams), but 3E works too.
/// </summary>
public sealed class McUdpClient : McClientBase
{
    private UdpClient? _udp;

    public McUdpClient(McOptions options, DeviceConfig device, ClientBuildContext context)
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

    protected override async Task<ReadOnlyMemory<byte>> TransactAsync(byte[] request, CancellationToken ct)
    {
        var udp = _udp ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");

        _ = await udp.SendAsync(request, ct).ConfigureAwait(false);
        var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        return result.Buffer;
    }
}
