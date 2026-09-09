using System.Net;
using System.Net.Sockets;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>
/// Scripted MC-protocol TCP server: reads complete 3E/4E request frames and answers them through
/// a test-provided responder. Captures every received request for golden-byte assertions.
/// </summary>
public sealed class ScriptedMcPlc : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public Func<byte[], byte[]> OnRequest { get; set; } = static _ => [];

    public List<byte[]> ReceivedRequests { get; } = [];

    public int Port { get; }

    public ScriptedMcPlc()
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
                    // client went away — fine for a test double
                }
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[1024];

        while (!ct.IsCancellationRequested && client.Connected)
        {
            // subheader (2) + optional serial (2) + route (5) + length (2)
            var first = await ReadAsync(stream, buffer, 0, 2, ct).ConfigureAwait(false);
            if (first == 0)
                return; // client closed

            bool is4E = buffer[0] == 0x54;
            int headerLength = is4E ? 11 : 9;
            await ReadAsync(stream, buffer, 2, headerLength - 2, ct).ConfigureAwait(false);

            // length field sits after subheader(2) + serial(4E: 2) + route(5)
            int lengthOffset = is4E ? 9 : 7;
            int contentLength = buffer[lengthOffset] | (buffer[lengthOffset + 1] << 8);
            int total = headerLength + contentLength;
            await ReadAsync(stream, buffer, headerLength, total - headerLength, ct).ConfigureAwait(false);

            var request = buffer[..total].ToArray();
            lock (ReceivedRequests)
            {
                ReceivedRequests.Add(request);
            }

            var response = OnRequest(request);
            if (response.Length > 0)
                await stream.WriteAsync(response, ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct).ConfigureAwait(false);
            if (read == 0)
                return total == 0 ? 0 : throw new EndOfStreamException();
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
