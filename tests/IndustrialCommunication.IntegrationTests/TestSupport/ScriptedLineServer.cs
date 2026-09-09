using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>
/// Line-based scripted TCP server for ASCII protocols: reads one CR/LF-terminated request line
/// and answers through a test-provided responder (responses always end with CR LF).
/// Pass strictCrLf: false when the client terminates commands with a bare CR (Mewtocol).
/// </summary>
public sealed class ScriptedLineServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly bool _strictCrLf = true;

    public Func<string, string> OnLine { get; set; } = static _ => "";

    public List<string> ReceivedLines { get; } = [];

    public int Port { get; }

    public ScriptedLineServer(bool strictCrLf = true)
    {
        _strictCrLf = strictCrLf;
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
        var buffer = new byte[256];
        var line = new StringBuilder();
        var lastWasCr = false;

        while (!ct.IsCancellationRequested && client.Connected)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return;

            for (int i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == 0x0A && lastWasCr)
                {
                    lastWasCr = false;

                    var request = line.ToString();
                    line.Clear();
                    await HandleLineAsync(stream, request, ct).ConfigureAwait(false);
                    continue;
                }
                if (b == 0x0D)
                {
                    lastWasCr = true;
                    if (!_strictCrLf)
                    {
                        // bare CR terminates the line (client uses CR-only framing)
                        var request = line.ToString();
                        line.Clear();
                        await HandleLineAsync(stream, request, ct).ConfigureAwait(false);
                    }
                    continue;
                }
                line.Append((char)b);
            }
        }
    }

    private async Task HandleLineAsync(NetworkStream stream, string request, CancellationToken ct)
    {
        lock (ReceivedLines)
        {
            ReceivedLines.Add(request);
        }

        var response = OnLine(request);
        if (response.Length > 0)
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), ct).ConfigureAwait(false);
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
