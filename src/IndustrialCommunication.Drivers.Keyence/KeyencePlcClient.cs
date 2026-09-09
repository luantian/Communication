using System.Net.Sockets;
using System.Text;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Keyence;

/// <summary>
/// Keyence KV series upper-computer-link client over TCP (default port 8501). Strictly lock-step:
/// one command in flight per connection (the base class serializes requests through the gate).
/// </summary>
public sealed class KeyencePlcClient : KeyenceClientBase
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public KeyencePlcClient(KeyenceOptions options, DeviceConfig device, ClientBuildContext context)
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

    protected override async Task<string> TransactAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");

        // The manual requires CR; CRLF is also accepted (verified against real captures) and
        // avoids ambiguity with devices that treat a lone trailing LF as a new empty command.
        var bytes = Encoding.ASCII.GetBytes(command.TrimEnd('\r') + "\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Responses end with CR LF.
        var line = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(ReadBuffer, ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The PLC closed the connection.");

            for (int i = 0; i < read; i++)
            {
                var b = ReadBuffer[i];
                if (b == 0x0A && LastByteWasCr)
                {
                    LastByteWasCr = false;
                    return line.ToString();
                }
                if (b == 0x0D)
                {
                    LastByteWasCr = true;
                    continue;
                }
                line.Append((char)b);
            }
        }
    }

    private readonly byte[] ReadBuffer = new byte[256];
    private bool LastByteWasCr;
}

/// <summary>
/// Keyence KV series upper-computer-link client over UDP: one command datagram, one response
/// datagram (response payload ends with CR LF, which is stripped before parsing).
/// </summary>
public sealed class KeyenceUdpClient : KeyenceClientBase
{
    private UdpClient? _udp;

    public KeyenceUdpClient(KeyenceOptions options, DeviceConfig device, ClientBuildContext context)
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

    protected override async Task<string> TransactAsync(string command, CancellationToken ct)
    {
        var udp = _udp ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");

        _ = await udp.SendAsync(Encoding.ASCII.GetBytes(command), ct).ConfigureAwait(false);
        var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(result.Buffer).TrimEnd('\r', '\n');
    }
}
