using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication;

/// <summary>
/// Background connection monitor: one loop over all enabled devices. Disconnected clients are
/// reconnected; connected clients get a heartbeat probe so half-open connections are noticed
/// even when no application traffic flows.
/// </summary>
internal sealed partial class ConnectionMonitor : IAsyncDisposable
{
    private readonly CommunicationHost _host;
    private readonly ConnectionMonitorConfig _config;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public ConnectionMonitor(CommunicationHost host, ConnectionMonitorConfig config, ILoggerFactory loggerFactory)
    {
        _host = host;
        _config = config;
        _logger = loggerFactory.CreateLogger<ConnectionMonitor>();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
            return;
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var name in _host.DeviceNames)
            {
                if (ct.IsCancellationRequested)
                    break;

                IPlcClient client;
                try
                {
                    client = _host.GetClient(name);
                }
                catch (CommunicationException ex)
                {
                    LogNoClient(name, ex.Message);
                    continue;
                }

                try
                {
                    if (client.IsConnected)
                    {
                        var probe = await client.HeartbeatAsync(ct).ConfigureAwait(false);
                        if (!probe.Success && probe.Kind != CommErrorKind.InvalidArgument)
                            LogHeartbeatFailed(name, probe.ToString());
                    }
                    else
                    {
                        await client.ConnectAsync(ct).ConfigureAwait(false);
                        LogReconnected(name);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (CommunicationException ex)
                {
                    LogReconnectFailed(name, ex.Message);
                }
            }

            try
            {
                await Task.Delay(_config.IntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Device}] client could not be created: {Reason}")]
    private partial void LogNoClient(string device, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Device}] heartbeat failed: {Reason}")]
    private partial void LogHeartbeatFailed(string device, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Device}] reconnected by the connection monitor")]
    private partial void LogReconnected(string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Device}] reconnect attempt failed: {Reason}")]
    private partial void LogReconnectFailed(string device, string reason);
}
