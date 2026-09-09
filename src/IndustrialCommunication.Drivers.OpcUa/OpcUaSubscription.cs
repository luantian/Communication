using Opc.Ua;
using Opc.Ua.Client;

namespace IndustrialCommunication.OpcUa;

/// <summary>A change notification for a subscribed node.</summary>
public sealed class OpcUaValueChangedEventArgs : EventArgs
{
    public OpcUaValueChangedEventArgs(string nodeId, object? value, DateTimeOffset timestamp, bool goodQuality)
    {
        NodeId = nodeId;
        Value = value;
        Timestamp = timestamp;
        GoodQuality = goodQuality;
    }

    public string NodeId { get; }
    public object? Value { get; }
    public DateTimeOffset Timestamp { get; }
    public bool GoodQuality { get; }
}

/// <summary>
/// Native OPC UA subscription (server push): monitored items deliver value changes without polling.
/// Create through <see cref="OpcUaPlcClient.CreateSubscription"/>; dispose to remove the subscription.
/// </summary>
public sealed class OpcUaSubscription : IAsyncDisposable, IDisposable
{
    private readonly ISession _session;
    private readonly Subscription _subscription;
    private readonly Action<OpcUaSubscription, OpcUaValueChangedEventArgs> _onChanged;
    private readonly List<MonitoredItem> _items = [];

        private readonly ITelemetryContext _telemetry;

    internal OpcUaSubscription(ISession session, ITelemetryContext telemetry, int publishingIntervalMs,
        Action<OpcUaSubscription, OpcUaValueChangedEventArgs> onChanged)
    {
        _session = session;
        _telemetry = telemetry;
        _onChanged = onChanged;
        _subscription = new Subscription(telemetry, new SubscriptionOptions
        {
            PublishingInterval = publishingIntervalMs,
            KeepAliveCount = 10,
            LifetimeCount = 60,
            PublishingEnabled = true,
        });
    }

    internal Subscription UnderlyingSubscription => _subscription;

    internal Task CreateAsync(CancellationToken ct) => _subscription.CreateAsync(ct);

    /// <summary>Adds a monitored item for a NodeId; the callback fires on every server-side change.</summary>
    public OpcUaSubscription Subscribe(string nodeId, int samplingIntervalMs = -1)
    {
        var item = new MonitoredItem(_telemetry, new MonitoredItemOptions
        {
            StartNodeId = NodeId.Parse(nodeId),
            AttributeId = Attributes.Value,
            DisplayName = nodeId,
            SamplingInterval = samplingIntervalMs,
            QueueSize = 10,
            DiscardOldest = true,
        });
        item.Notification += OnNotification;
        _subscription.AddItem(item);
        _items.Add(item);
        return this;
    }

    /// <summary>Applies pending additions/removals to the server.</summary>
    public Task ApplyChangesAsync(CancellationToken ct = default) =>
        _subscription.ApplyChangesAsync(ct);

    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        try
        {
            if (e.NotificationValue is not MonitoredItemNotification notification || notification.Value is null)
                return;

            var value = notification.Value;
            _onChanged(this, new OpcUaValueChangedEventArgs(
                item.StartNodeId.ToString(),
                value.Value,
                value.SourceTimestamp == DateTime.MinValue ? value.ServerTimestamp : value.SourceTimestamp,
                StatusCode.IsGood(value.StatusCode)));
        }
        catch
        {
            // notifications must never take the publish loop down
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _items)
            item.Notification -= OnNotification;
        _items.Clear();

        try
        {
            if (_subscription.Created)
                await _session.RemoveSubscriptionAsync(_subscription, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // disposal must not throw
        }
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
