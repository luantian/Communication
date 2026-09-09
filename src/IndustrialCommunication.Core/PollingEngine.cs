using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialCommunication;

/// <summary>Raised after every poll cycle; <see cref="Changed"/> carries only the points whose value changed (the first poll reports everything).</summary>
public sealed class PollEventArgs : EventArgs
{
    public required string GroupName { get; init; }
    public required GroupReadResult Result { get; init; }
    public required IReadOnlyDictionary<string, object?> Changed { get; init; }
}

/// <summary>
/// Polling engine ("subscription" on top of polling): runs one loop per configured poll group,
/// reads the group's points through the device client and raises change events.
/// Handlers run on the thread pool and must not block.
/// </summary>
public sealed partial class PollingEngine : IAsyncDisposable, IDisposable
{
    private readonly CommunicationHost _host;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Dictionary<string, object?>> _lastValues = new(StringComparer.Ordinal);
    private readonly List<(PollGroupConfig Group, Task Loop)> _loops = [];
    private readonly List<Subscription> _subscriptions = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;

    public event EventHandler<PollEventArgs>? PollCompleted;

    public PollingEngine(CommunicationHost host, ILoggerFactory? loggerFactory = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PollingEngine>();
    }

    /// <summary>Poll group names from the configuration, in order.</summary>
    public IReadOnlyList<string> GroupNames =>
        _host.Config.PollGroups.Select(static g => g.Name).ToArray();

    /// <summary>Starts one loop per poll group. Idempotent; groups referencing unknown devices fail their own loop, not the start.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_started)
                return Task.CompletedTask;
            _started = true;

            foreach (var group in _host.Config.PollGroups)
            {
                var errors = group.Validate();
                if (errors.Count > 0)
                {
                    foreach (var error in errors)
                        LogGroupInvalid(group.Name, error);
                    continue;
                }

                var loop = Task.Run(() => RunGroupAsync(group, ct), CancellationToken.None);
                _loops.Add((group, loop));
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Stops all loops and waits for them to finish.</summary>
    public async Task StopAsync()
    {
        Task[] loops;
        lock (_gate)
        {
            loops = [.. _loops.Select(static l => l.Loop)];
            _loops.Clear();
            _started = false;
        }

        _cts.Cancel();
        foreach (var loop in loops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Registers a callback for changes of a single point (device-agnostic, point names are unique per group). Dispose the handle to unsubscribe.</summary>
    public IDisposable Subscribe(string pointName, Action<object?> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var subscription = new Subscription(this, pointName, onChange);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }
        return subscription;
    }

    /// <summary>Sync disposal bridge for using statements; prefer <c>await using</c>.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task RunGroupAsync(PollGroupConfig group, CancellationToken callerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, _cts.Token);
        var ct = linked.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = _host.GetClient(group.Device);
                var result = await client.ReadGroupAsync([.. group.Points], ct).ConfigureAwait(false);
                var changed = RecordAndDiff(group.Name, result);
                RaisePollCompleted(new PollEventArgs
                {
                    GroupName = group.Name,
                    Result = result,
                    Changed = changed,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poll loop must never die — log and keep cycling.
                LogCycleFailed(group.Name, ex);
            }

            try
            {
                await Task.Delay(group.IntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Dictionary<string, object?> RecordAndDiff(string groupName, GroupReadResult result)
    {
        lock (_gate)
        {
            if (!_lastValues.TryGetValue(groupName, out var bucket))
            {
                bucket = new Dictionary<string, object?>(StringComparer.Ordinal);
                _lastValues[groupName] = bucket;
            }

            var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
            var firstPoll = bucket.Count == 0;

            foreach (var (pointName, status) in result.Statuses)
            {
                if (!status.Success)
                    continue; // keep the last good value on failures

                if (!result.TryGetValue(pointName, out var value))
                    continue;

                if (firstPoll || !ValuesEqual(bucket.GetValueOrDefault(pointName), value))
                    changed[pointName] = value;
                bucket[pointName] = value;
            }

            return changed;
        }
    }

    private void RaisePollCompleted(PollEventArgs args)
    {
        var handler = Volatile.Read(ref PollCompleted);
        handler?.Invoke(this, args);

        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions];
        }

        foreach (var subscription in subscriptions)
        {
            if (args.Changed.TryGetValue(subscription.PointName, out var value))
                subscription.Invoke(value);
        }
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is Array left && b is Array right)
        {
            if (left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (!Equals(left.GetValue(i), right.GetValue(i)))
                    return false;
            }
            return true;
        }

        return a?.Equals(b) == true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Poll group {Group}: {Error}")]
    private partial void LogGroupInvalid(string group, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Poll group {Group} cycle failed")]
    private partial void LogCycleFailed(string group, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscription handler for point {Point} threw")]
    private partial void LogSubscriptionThrew(string point, Exception ex);

    private sealed class Subscription(PollingEngine engine, string pointName, Action<object?> onChange) : IDisposable
    {
        public string PointName => pointName;

        public void Invoke(object? value)
        {
            try
            {
                onChange(value);
            }
            catch (Exception ex)
            {
                engine.LogSubscriptionThrew(pointName, ex);
            }
        }

        public void Dispose()
        {
            lock (engine._gate)
            {
                engine._subscriptions.Remove(this);
            }
        }
    }
}
