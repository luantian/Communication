using System.Net;
using System.Net.Sockets;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>Datagram-based scripted UDP server: every received datagram is answered through a test-provided responder.</summary>
public sealed class ScriptedUdpPlc : IAsyncDisposable
{
    private readonly UdpClient _udp = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public Func<byte[], byte[]> OnDatagram { get; set; } = static _ => [];

    public List<byte[]> ReceivedDatagrams { get; } = [];

    public int Port { get; }

    public ScriptedUdpPlc()
    {
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            lock (ReceivedDatagrams)
            {
                ReceivedDatagrams.Add(received.Buffer);
            }

            var reply = OnDatagram(received.Buffer);
            if (reply.Length > 0)
            {
                try
                {
                    await _udp.SendAsync(reply, received.RemoteEndPoint, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
