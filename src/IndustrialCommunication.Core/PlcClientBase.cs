using System.Net.Sockets;
using System.Text;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialCommunication;

/// <summary>
/// Template-method base for all drivers: request serialization (one operation at a time per client),
/// lazy reconnect, per-operation timeout with forced disconnect (half-open protection), exception →
/// <see cref="CommResult"/> mapping, logging and connection state events.
/// Drivers implement <see cref="DoConnectAsync"/>, <see cref="DoDisconnectAsync"/>, the four
/// <c>Do*</c> primitives and <see cref="ParseAddress"/>.
/// </summary>
public abstract partial class PlcClientBase : IPlcClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DeviceRuntimeOptions _runtime;
    private readonly ILogger _logger;
    private volatile bool _connected;
    private volatile bool _disposed;

    protected PlcClientBase(
        DeviceConfig device,
        DeviceRuntimeOptions runtime,
        DataLayout dataLayout,
        Encoding stringEncoding,
        ILogger? logger)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        DataLayout = dataLayout;
        StringEncoding = stringEncoding ?? Encoding.UTF8;
        _logger = logger ?? NullLogger.Instance;
    }

    protected DeviceConfig Device { get; }
    protected DeviceRuntimeOptions Runtime => _runtime;
    protected DataLayout DataLayout { get; }
    protected Encoding StringEncoding { get; }
    protected ILogger Logger => _logger;

    public string DeviceName => Device.Name;
    public bool IsConnected => _connected;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    // =====================================================================
    // Connection lifecycle
    // =====================================================================

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected)
                return;
            await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync("disconnect requested").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisconnectCoreAsync("disposed").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _gate.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    // =====================================================================
    // Primitive operations (gate + reconnect + timeout applied here)
    // =====================================================================

    public Task<CommResult<bool[]>> ReadBitsAsync(string address, ushort count, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (count == 0)
            return Task.FromResult(CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null, "count must be at least 1."));

        return ExecuteAsync(async token => await DoReadBitsAsync(ParseAddress(address) with { Raw = address }, count, token).ConfigureAwait(false), ct);
    }

    public Task<CommResult> WriteBitsAsync(string address, IReadOnlyList<bool> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return Task.FromResult(CommResult.Fail(CommErrorKind.InvalidArgument, null, "values must contain at least one element."));

        return ExecuteAsync(async token => await DoWriteBitsAsync(ParseAddress(address) with { Raw = address }, values, token).ConfigureAwait(false), ct);
    }

    public Task<CommResult<ushort[]>> ReadWordsAsync(string address, ushort count, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (count == 0)
            return Task.FromResult(CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null, "count must be at least 1."));

        return ExecuteAsync(async token => await DoReadWordsAsync(ParseAddress(address) with { Raw = address }, count, token).ConfigureAwait(false), ct);
    }

    public Task<CommResult> WriteWordsAsync(string address, IReadOnlyList<ushort> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return Task.FromResult(CommResult.Fail(CommErrorKind.InvalidArgument, null, "values must contain at least one element."));

        return ExecuteAsync(async token => await DoWriteWordsAsync(ParseAddress(address) with { Raw = address }, values, token).ConfigureAwait(false), ct);
    }

    // =====================================================================
    // Typed layer (default implementation over the primitives)
    // =====================================================================

    public virtual Task<CommResult<T>> ReadAsync<T>(string address, CancellationToken ct = default) where T : struct
    {
        ArgumentNullException.ThrowIfNull(address);
        var valueType = ValueTypeMap.FromClrType(typeof(T));

        return ExecuteAsync(async token =>
        {
            if (valueType == PlcValueType.Bit)
            {
                var bits = await DoReadBitsAsync(ParseAddress(address), 1, token).ConfigureAwait(false);
                if (!bits.Success)
                    return CommResult<T>.Fail(bits.Kind, bits.ErrorCode, bits.Message);
                return CommResult<T>.Ok((T)(object)bits.Value[0]);
            }

            var words = await DoReadWordsAsync(
                ParseAddress(address),
                checked((ushort)ValueTypeMap.WordCount(valueType)),
                token).ConfigureAwait(false);
            if (!words.Success)
                return CommResult<T>.Fail(words.Kind, words.ErrorCode, words.Message);
            return CommResult<T>.Ok(ValueCodec.Decode<T>(words.Value, DataLayout));
        }, ct);
    }

    public virtual Task<CommResult> WriteAsync<T>(string address, T value, CancellationToken ct = default) where T : struct
    {
        ArgumentNullException.ThrowIfNull(address);
        var valueType = ValueTypeMap.FromClrType(typeof(T));

        return ExecuteAsync(async token =>
        {
            if (valueType == PlcValueType.Bit)
                return await DoWriteBitsAsync(ParseAddress(address), [(bool)(object)value], token).ConfigureAwait(false);

            var encoded = ValueCodec.Encode(value, DataLayout);
            return await DoWriteWordsAsync(ParseAddress(address), encoded, token).ConfigureAwait(false);
        }, ct);
    }

    public virtual Task<CommResult<string>> ReadStringAsync(string address, ushort wordLength, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (wordLength == 0)
            return Task.FromResult(CommResult<string>.Fail(CommErrorKind.InvalidArgument, null, "wordLength must be at least 1."));

        return ExecuteAsync(async token =>
        {
            var words = await DoReadWordsAsync(ParseAddress(address), wordLength, token).ConfigureAwait(false);
            if (!words.Success)
                return CommResult<string>.Fail(words.Kind, words.ErrorCode, words.Message);
            return CommResult<string>.Ok(ValueCodec.DecodeString(words.Value, StringEncoding));
        }, ct);
    }

    public virtual Task<CommResult> WriteStringAsync(string address, string value, ushort wordLength, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(value);
        if (wordLength == 0)
            return Task.FromResult(CommResult.Fail(CommErrorKind.InvalidArgument, null, "wordLength must be at least 1."));

        return ExecuteAsync(async token =>
        {
            var encoded = ValueCodec.EncodeString(value, wordLength, StringEncoding);
            return await DoWriteWordsAsync(ParseAddress(address), encoded, token).ConfigureAwait(false);
        }, ct);
    }

    public virtual async Task<GroupReadResult> ReadGroupAsync(IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(points);

        var values = new Dictionary<string, object?>(points.Count, StringComparer.Ordinal);
        var statuses = new Dictionary<string, CommResult>(points.Count, StringComparer.Ordinal);

        foreach (var point in points)
        {
            ArgumentNullException.ThrowIfNull(point, nameof(points));
            var (status, value) = await ReadPointAsync(point, ct).ConfigureAwait(false);
            statuses[point.Name] = status;
            values[point.Name] = value;
        }

        return new GroupReadResult(values, statuses);
    }

    // =====================================================================
    // Driver contract
    // =====================================================================

    /// <summary>Opens the transport. Must clean up partially opened resources when it throws.</summary>
    protected abstract Task DoConnectAsync(CancellationToken ct);

    /// <summary>Closes and releases the transport. Must be idempotent and safe on a never-connected client.</summary>
    protected abstract Task DoDisconnectAsync();

    /// <summary>Parses a driver address string; throw <see cref="FormatException"/> with a clear message on bad input.</summary>
    protected abstract DeviceAddress ParseAddress(string address);

    protected abstract Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct);
    protected abstract Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct);
    protected abstract Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct);
    protected abstract Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct);

    // =====================================================================
    // Execution core: gate → lazy reconnect → timeout → exception mapping
    // =====================================================================

    protected async Task<CommResult> ExecuteAsync(Func<CancellationToken, Task<CommResult>> op, CancellationToken ct)
    {
        var wrapped = await ExecuteAsync<object?>(async token =>
        {
            var result = await op(token).ConfigureAwait(false);
            return result.Success
                ? CommResult<object?>.Ok(null)
                : CommResult<object?>.Fail(result.Kind, result.ErrorCode, result.Message);
        }, ct).ConfigureAwait(false);

        return wrapped.Success ? CommResult.Ok() : CommResult.Fail(wrapped.Kind, wrapped.ErrorCode, wrapped.Message);
    }

    protected async Task<CommResult<T>> ExecuteAsync<T>(Func<CancellationToken, Task<CommResult<T>>> op, CancellationToken ct)
    {
        if (_disposed)
            return CommResult<T>.Fail(CommErrorKind.Disposed, null, $"[{Device.Name}] client is disposed.");

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommResult<T>.Fail(CommErrorKind.Cancelled, null, "operation cancelled while waiting for the connection.");
        }

        try
        {
            if (!_connected)
            {
                if (!_runtime.AutoReconnect)
                    return CommResult<T>.Fail(CommErrorKind.NotConnected, null, $"[{Device.Name}] not connected.");

                RaiseState(ConnectionState.Reconnecting, "lazy reconnect before operation");
                try
                {
                    await ConnectCoreAsync(ct).ConfigureAwait(false);
                }
                catch (CommunicationException ex)
                {
                    return CommResult<T>.Fail(ex.Kind, ex.ErrorCode, ex.Message);
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_runtime.TimeoutMs);

            try
            {
                var operation = op(timeoutCts.Token);

                // Hard-timeout race: some underlying libraries do not honor cancellation tokens.
                // If the operation ignores the cancelled token, close the transport to abort it.
                var timeout = Task.Delay(_runtime.TimeoutMs + HardTimeoutGraceMs, timeoutCts.Token);
                var winner = await Task.WhenAny(operation, timeout).ConfigureAwait(false);
                if (winner != operation)
                {
                    await DisconnectCoreAsync("operation timeout").ConfigureAwait(false);
                    ObserveFault(operation);
                    return CommResult<T>.Fail(CommErrorKind.Timeout, null,
                        $"[{Device.Name}] operation timed out after {_runtime.TimeoutMs} ms; connection was closed.");
                }

                return await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Own timeout fired: the connection is now untrusted (half-open risk) — force disconnect.
                await DisconnectCoreAsync("operation timeout").ConfigureAwait(false);
                return CommResult<T>.Fail(CommErrorKind.Timeout, null,
                    $"[{Device.Name}] operation timed out after {_runtime.TimeoutMs} ms; connection was closed.");
            }
            catch (TimeoutException ex)
            {
                await DisconnectCoreAsync("operation timeout").ConfigureAwait(false);
                return CommResult<T>.Fail(CommErrorKind.Timeout, null,
                    $"[{Device.Name}] {ex.Message}; connection was closed.");
            }
            catch (Exception ex) when (IsTransportDamage(ex))
            {
                await DisconnectCoreAsync(ex.Message).ConfigureAwait(false);
                return CommResult<T>.Fail(CommErrorKind.ConnectionLost, null,
                    $"[{Device.Name}] connection lost: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                return CommResult<T>.Fail(CommErrorKind.Cancelled, null, "operation cancelled.");
            }
            catch (FormatException ex)
            {
                return CommResult<T>.Fail(CommErrorKind.InvalidAddress, null, $"[{Device.Name}] {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                return CommResult<T>.Fail(CommErrorKind.InvalidArgument, null, $"[{Device.Name}] {ex.Message}");
            }
            catch (CommunicationException ex)
            {
                return CommResult<T>.Fail(ex.Kind, ex.ErrorCode, ex.Message);
            }
            catch (Exception ex)
            {
                LogUnexpectedError(Device.Name, ex);
                return CommResult<T>.Fail(CommErrorKind.ProtocolError, null,
                    $"[{Device.Name}] {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Transports that make the connection unusable after the exception escapes.</summary>
    protected static bool IsTransportDamage(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException;

    /// <summary>
    /// Cheap periodic probe used by the connection monitor. Drivers override this with a single
    /// canonical readable point; failure marks the connection down (through the normal error mapping).
    /// </summary>
    protected virtual Task<CommResult> DoHeartbeatAsync(CancellationToken ct) =>
        Task.FromResult(CommResult.Fail(CommErrorKind.InvalidArgument, null,
            "This driver does not define a heartbeat; the monitor only reconnects when disconnected."));

    /// <summary>Runs one heartbeat probe through the standard execution pipeline (gate, timeout, reconnect).</summary>
    public Task<CommResult> HeartbeatAsync(CancellationToken ct = default) =>
        ExecuteAsync(token => DoHeartbeatAsync(token), ct);

    /// <summary>Grace added to the configured timeout before the hard race fires.</summary>
    protected const int HardTimeoutGraceMs = 250;

    /// <summary>Observes a leftover faulted task so it never surfaces as UnobservedTaskException.</summary>
    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // =====================================================================
    // Internals
    // =====================================================================

    private async Task ConnectCoreAsync(CancellationToken callerCt)
    {
        int attempts = Math.Max(1, _runtime.ConnectRetries + 1);

        for (int attempt = 1; ; attempt++)
        {
            callerCt.ThrowIfCancellationRequested();
            RaiseState(ConnectionState.Connecting, $"attempt {attempt}/{attempts}");

            CommErrorKind kind;
            string message;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
                timeoutCts.CancelAfter(_runtime.ConnectTimeoutMs);

                await DoConnectAsync(timeoutCts.Token).ConfigureAwait(false);

                _connected = true;
                LogConnected(Device.Name);
                RaiseState(ConnectionState.Connected, null);
                return;
            }
            catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
            {
                kind = CommErrorKind.Timeout;
                message = $"connect timed out after {_runtime.ConnectTimeoutMs} ms";
            }
            catch (Exception ex) when (IsTransportDamage(ex))
            {
                kind = CommErrorKind.ConnectionLost;
                message = ex.Message;
            }
            catch (Exception ex)
            {
                kind = CommErrorKind.ProtocolError;
                message = $"{ex.GetType().Name}: {ex.Message}";
            }

            await DisconnectCoreAsync($"connect failed: {message}").ConfigureAwait(false);

            if (attempt >= attempts)
                throw new CommunicationException(kind, null, $"[{Device.Name}] {message}");

            LogConnectRetry(Device.Name, attempt, attempts, message, _runtime.RetryIntervalMs);
            await Task.Delay(_runtime.RetryIntervalMs, callerCt).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Device}] connected")]
    private partial void LogConnected(string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Device}] disconnected: {Reason}")]
    private partial void LogDisconnected(string device, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Device}] connect attempt {Attempt}/{Attempts} failed ({Reason}); retrying in {Interval} ms")]
    private partial void LogConnectRetry(string device, int attempt, int attempts, string reason, int interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{Device}] unexpected error during operation")]
    private partial void LogUnexpectedError(string device, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Device}] error while disconnecting")]
    private partial void LogDisconnectError(string device, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Device}] ConnectionStateChanged handler threw")]
    private partial void LogHandlerThrew(string device, Exception ex);

    private async Task DisconnectCoreAsync(string? reason)
    {
        if (!_connected)
            return;

        _connected = false;
        try
        {
            await DoDisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisconnectError(Device.Name, ex);
        }

        LogDisconnected(Device.Name, reason ?? "unknown");
        RaiseState(ConnectionState.Disconnected, reason);
    }

    private async Task<(CommResult Status, object? Value)> ReadPointAsync(DevicePoint point, CancellationToken ct)
    {
        int length = Math.Max(1, (int)point.ArrayLength);

        if (point.ValueType == PlcValueType.String)
        {
            var str = await ReadStringAsync(point.Address, checked((ushort)length), ct).ConfigureAwait(false);
            return (str.WithoutValue(), str.Success ? str.Value : null);
        }

        if (point.ValueType == PlcValueType.Bit)
        {
            var bits = await ReadBitsAsync(point.Address, checked((ushort)length), ct).ConfigureAwait(false);
            if (!bits.Success)
                return (bits.WithoutValue(), null);
            return length == 1 ? (bits.WithoutValue(), bits.Value[0]) : (bits.WithoutValue(), bits.Value);
        }

        int wordsPerElement = ValueTypeMap.WordCount(point.ValueType);
        var words = await ReadWordsAsync(point.Address, checked((ushort)(wordsPerElement * length)), ct).ConfigureAwait(false);
        if (!words.Success)
            return (words.WithoutValue(), null);

        if (length == 1)
            return (words.WithoutValue(), ValueCodec.DecodeObject(point.ValueType, words.Value, DataLayout));

        var elementType = ValueTypeMap.ClrType(point.ValueType);
        var array = Array.CreateInstance(elementType, length);
        for (int i = 0; i < length; i++)
            array.SetValue(ValueCodec.DecodeObject(point.ValueType, words.Value.AsSpan(i * wordsPerElement, wordsPerElement), DataLayout), i);
        return (words.WithoutValue(), array);
    }

    private void RaiseState(ConnectionState state, string? reason)
    {
        var handler = ConnectionStateChanged;
        if (handler is null)
            return;

        var args = new ConnectionStateChangedEventArgs(state, reason);
        _ = Task.Run(() =>
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                LogHandlerThrew(Device.Name, ex);
            }
        }, CancellationToken.None);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
