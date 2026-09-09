using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>
/// Length-prefixed scripted TCP server: reads the fixed header plus the little-endian length field
/// at <c>LengthAt</c>, and answers through a test responder (null = silence). Suits protocols like
/// LS FEnet (20-byte header, LE length at offset 16).
/// </summary>
public sealed class ScriptedLengthPrefixedServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public int HeaderLength { get; }

    public int LengthAt { get; }

    public Func<byte[], byte[]?>? OnFrame { get; set; }

    public List<byte[]> ReceivedFrames { get; } = [];

    public int Port { get; }

    public ScriptedLengthPrefixedServer(int headerLength, int lengthAt)
    {
        HeaderLength = headerLength;
        LengthAt = lengthAt;
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

        while (!ct.IsCancellationRequested && client.Connected)
        {
            var header = new byte[HeaderLength];
            if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
                return;

            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(LengthAt));
            var frame = new byte[HeaderLength + length];
            header.CopyTo(frame, 0);
            if (length > 0 && !await ReadExactlyAsync(stream, frame.AsMemory(HeaderLength), ct).ConfigureAwait(false))
                return;

            lock (ReceivedFrames)
            {
                ReceivedFrames.Add(frame);
            }

            var reply = OnFrame?.Invoke(frame);
            if (reply is { Length: > 0 })
                await stream.WriteAsync(reply, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
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
