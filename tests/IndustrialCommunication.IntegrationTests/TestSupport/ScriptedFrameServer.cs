using System.Net;
using System.Net.Sockets;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>
/// Fixed-frame-length scripted TCP server: reads exactly <c>frameLength</c> bytes per request and
/// answers through a test-provided responder (null = stay silent). GE SRTP's 56-byte framing
/// makes this trivial; multi-frame replies are returned concatenated.
/// </summary>
public sealed class ScriptedFrameServer : IAsyncDisposable
{
    private const int FrameLength = 56; // SRTP fixed header size

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    /// <summary>Receives one request frame; the reply (possibly longer than one frame) or null for silence.</summary>
    public Func<byte[], byte[]?>? OnFrame { get; set; }

    public List<byte[]> ReceivedFrames { get; } = [];

    public int Port { get; }

    public ScriptedFrameServer()
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
        var buffer = new byte[FrameLength];

        while (!ct.IsCancellationRequested && client.Connected)
        {
            int total = 0;
            while (total < FrameLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                    return;
                total += read;
            }

            var request = (byte[])buffer.Clone();
            lock (ReceivedFrames)
            {
                ReceivedFrames.Add(request);
            }

            var reply = OnFrame?.Invoke(request);
            if (reply is { Length: > 0 })
                await stream.WriteAsync(reply, ct).ConfigureAwait(false);
        }
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
