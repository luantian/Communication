using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;

namespace IndustrialCommunication.Mqtt.Sparkplug;

/// <summary>Sparkplug B bridge settings: topics are spBv1.0/{Group}/{Type}/{EdgeNode}[/{Device}].</summary>
public sealed class SparkplugBridgeOptions
{
    /// <summary>Sparkplug group id (topic namespace part).</summary>
    public required string Group { get; init; }

    /// <summary>Edge node id — the bridge itself is the edge node.</summary>
    public required string EdgeNode { get; init; }

    /// <summary>Devices to publish births for; each maps to a CommunicationHost device of the same name.</summary>
    public required IReadOnlyList<string> Devices { get; init; }
}

/// <summary>
/// Sparkplug B edge node over the MQTT bridge: connect (with NDEATH will) → NBIRTH → DBIRTH per device;
/// data changes publish as DDATA through <see cref="Attach"/>; NCMD rebirth and DCMD writes are honoured.
/// </summary>
public sealed partial class SparkplugBridge : IAsyncDisposable, IDisposable
{
    private readonly CommunicationHost _host;
    private readonly MqttBridgeOptions _mqtt;
    private readonly SparkplugBridgeOptions _sparkplug;
    private readonly ILogger _logger;
    private readonly MQTTnet.Client.IMqttClient _client;
    private readonly byte _bdSeq;
    private byte _seq;
    private PollingEngine? _attachedEngine;

    public SparkplugBridge(CommunicationHost host, MqttBridgeOptions mqtt, SparkplugBridgeOptions sparkplug,
        ILoggerFactory? loggerFactory = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _sparkplug = sparkplug ?? throw new ArgumentNullException(nameof(sparkplug));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SparkplugBridge>();
        _client = new MqttFactory().CreateMqttClient();
        _bdSeq = (byte)Random.Shared.Next(256);
    }

    private string Topic(string type) => $"spBv1.0/{_sparkplug.Group}/{type}/{_sparkplug.EdgeNode}";

    private string Topic(string type, string device) => $"spBv1.0/{_sparkplug.Group}/{type}/{_sparkplug.EdgeNode}/{device}";

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Connect with the NDEATH will message carrying the birth/death sequence number.
        var deathPayload = SparkplugProto.EncodePayload(
            null,
            [new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, LongValue = _bdSeq }],
            _seq);

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId(_mqtt.ClientId)
            .WithWillTopic(Topic("NDEATH"))
            .WithWillPayload(deathPayload)
            .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(_mqtt.Username))
            builder = builder.WithCredentials(_mqtt.Username, _mqtt.Password ?? string.Empty);

        await _client.ConnectAsync(builder.Build(), ct).ConfigureAwait(false);

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await HandleMessageAsync(e.ApplicationMessage.Topic,
                    e.ApplicationMessage.PayloadSegment.ToArray(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCommandFailed(ex.Message);
            }
        };

        await _client.SubscribeAsync(Topic("NCMD"), MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct)
            .ConfigureAwait(false);
        await _client.SubscribeAsync($"spBv1.0/{_sparkplug.Group}/DCMD/{_sparkplug.EdgeNode}/+",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct).ConfigureAwait(false);

        await PublishBirthsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Wires poll-group changes into per-device DDATA publications.</summary>
    public void Attach(PollingEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_attachedEngine is not null)
            throw new InvalidOperationException("Only one polling engine can be attached.");

        _attachedEngine = engine;
        engine.PollCompleted += OnPollCompleted;
    }

    private async void OnPollCompleted(object? sender, PollEventArgs e)
    {
        try
        {
            if (e.Changed.Count == 0)
                return;

            var metrics = new List<SparkplugMetric>();
            foreach (var (name, value) in e.Changed)
                metrics.Add(ToMetric(name, value));

            var payload = SparkplugProto.EncodePayload(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), metrics, NextSeq());
            await PublishAsync(Topic("DDATA", e.GroupName), payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex.Message);
        }
    }

    private async Task PublishBirthsAsync(CancellationToken ct)
    {
        // NBIRTH: node metrics (bdSeq + rebirth-required sequence restart)
        _seq = 0;
        var birth = SparkplugProto.EncodePayload(
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, LongValue = _bdSeq }],
            _seq);
        await PublishAsync(Topic("NBIRTH"), birth).ConfigureAwait(false);

        foreach (var device in _sparkplug.Devices)
        {
            var deviceBirth = SparkplugProto.EncodePayload(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], NextSeq());
            await PublishAsync(Topic("DBIRTH", device), deviceBirth).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(string topic, byte[] payload, CancellationToken ct)
    {
        var (_, metrics) = SparkplugProto.DecodePayload(payload);

        if (topic == Topic("NCMD"))
        {
            foreach (var metric in metrics)
            {
                if (metric.Name == "Node Control/Rebirth" && metric.BooleanValue)
                {
                    LogRebirth();
                    await PublishBirthsAsync(ct).ConfigureAwait(false);
                }
            }
            return;
        }

        // DCMD: spBv1.0/{group}/DCMD/{edge}/{device} → write the metric to that device
        if (topic.StartsWith($"spBv1.0/{_sparkplug.Group}/DCMD/{_sparkplug.EdgeNode}/", StringComparison.Ordinal))
        {
            var device = topic[(topic.LastIndexOf('/') + 1)..];
            foreach (var metric in metrics)
            {
                var write = await WriteMetricAsync(device, metric).ConfigureAwait(false);
                if (!write)
                    LogCommandFailed($"write of metric '{metric.Name}' to device '{device}' failed");
            }
        }
    }

    private async Task<bool> WriteMetricAsync(string device, SparkplugMetric metric)
    {
        try
        {
            var client = _host.GetClient(device);
            return metric.DataType switch
            {
                SparkplugDataType.Boolean => (await client.WriteAsync(metric.Name, metric.BooleanValue).ConfigureAwait(false)).Success,
                SparkplugDataType.Float or SparkplugDataType.Double => (await client.WriteAsync(metric.Name, metric.DoubleValue).ConfigureAwait(false)).Success,
                SparkplugDataType.String or SparkplugDataType.Text => (await client.WriteStringAsync(metric.Name, metric.StringValue, 32).ConfigureAwait(false)).Success,
                _ => (await client.WriteAsync(metric.Name, metric.LongValue).ConfigureAwait(false)).Success,
            };
        }
        catch (CommunicationException ex)
        {
            LogCommandFailed(ex.Message);
            return false;
        }
    }

    private static SparkplugMetric ToMetric(string name, object? value)
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            bool b => new SparkplugMetric { Name = name, TimestampMs = timestamp, DataType = SparkplugDataType.Boolean, BooleanValue = b },
            short or ushort or int or uint or long => new SparkplugMetric { Name = name, TimestampMs = timestamp, DataType = SparkplugDataType.Int64, LongValue = Convert.ToInt64(value, invariant) },
            float or double => new SparkplugMetric { Name = name, TimestampMs = timestamp, DataType = SparkplugDataType.Double, DoubleValue = Convert.ToDouble(value, invariant) },
            string s => new SparkplugMetric { Name = name, TimestampMs = timestamp, DataType = SparkplugDataType.String, StringValue = s },
            _ => new SparkplugMetric { Name = name, TimestampMs = timestamp, DataType = SparkplugDataType.String, StringValue = value?.ToString() ?? string.Empty },
        };
    }

    private byte NextSeq() => _seq = (byte)((_seq + 1) % 256);

    private async Task PublishAsync(string topic, byte[] payload)
    {
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sparkplug command failed: {Reason}")]
    private partial void LogCommandFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sparkplug publish failed: {Reason}")]
    private partial void LogPublishFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sparkplug rebirth requested")]
    private partial void LogRebirth();

    public async ValueTask DisposeAsync()
    {
        if (_attachedEngine is not null)
            _attachedEngine.PollCompleted -= OnPollCompleted;

        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build()).ConfigureAwait(false);
        }
        catch
        {
            // disposal must not throw
        }
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
