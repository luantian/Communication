using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;

namespace IndustrialCommunication.Mqtt;

/// <summary>MQTT bridge settings (the <c>mqtt</c> node of the configuration file).</summary>
public sealed class MqttBridgeOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "industrial-communication";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string TopicPrefix { get; set; } = "industrial";

    /// <summary>Enables TLS (MQTT over TLS, typically port 8883).</summary>
    public bool Tls { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host))
            errors.Add("mqtt.host is required.");
        if (Port is < 1 or > 65535)
            errors.Add($"mqtt.port {Port} is out of range.");
        return errors;
    }

    public static MqttBridgeOptions FromJsonElement(JsonElement element)
    {
        var options = element.Deserialize<MqttBridgeOptions>(CommunicationConfigLoader.JsonOptions)
            ?? throw new CommunicationException("The mqtt node could not be deserialized.");
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new CommunicationException($"Invalid mqtt node: {string.Join("; ", errors)}");
        return options;
    }
}

/// <summary>
/// MQTT cloud bridge: publishes every poll-group cycle as JSON to <c>{prefix}/groups/{group}</c>
/// (only changed points plus a timestamp) and accepts write commands on <c>{prefix}/commands/write</c>
/// with payload <c>{ "device": "...", "address": "...", "value": ..., "type": "Int16" }</c>.
/// </summary>
public sealed partial class MqttBridge : IAsyncDisposable, IDisposable
{
    private readonly CommunicationHost _host;
    private readonly MqttBridgeOptions _options;
    private readonly ILogger _logger;
    private readonly IMqttClient _client;
    private PollingEngine? _attachedEngine;

    public MqttBridge(CommunicationHost host, MqttBridgeOptions options, ILoggerFactory? loggerFactory = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MqttBridge>();
        _client = new MqttFactory().CreateMqttClient();
    }

    public string GroupsTopic => $"{_options.TopicPrefix}/groups/+";

    public string CommandTopic => $"{_options.TopicPrefix}/commands/write";

    public string ResultTopic => $"{_options.TopicPrefix}/commands/result";

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId);
        if (!string.IsNullOrEmpty(_options.Username))
            builder = builder.WithCredentials(_options.Username, _options.Password ?? string.Empty);
        if (_options.Tls)
            builder = builder.WithTlsOptions(o => o.UseTls());

        await _client.ConnectAsync(builder.Build(), ct).ConfigureAwait(false);

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await HandleCommandAsync(e.ApplicationMessage.PayloadSegment.ToArray(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCommandFailed(ex.Message);
            }
        };
        await _client.SubscribeAsync(CommandTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Wires the polling engine's cycles into MQTT publications.</summary>
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
            var payload = new JsonObject
            {
                ["group"] = e.GroupName,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["changed"] = ToJson(e.Changed),
                ["ok"] = e.Result.AllSuccess,
            };

            await PublishAsync($"{_options.TopicPrefix}/groups/{e.GroupName}", payload.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex.Message);
        }
    }

    private async Task HandleCommandAsync(byte[] payload, CancellationToken ct)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            await PublishResultAsync(null, false, $"invalid JSON: {ex.Message}").ConfigureAwait(false);
            return;
        }

        var device = node?["device"]?.GetValue<string>();
        var address = node?["address"]?.GetValue<string>();
        var value = node?["value"];
        var type = node?["type"]?.GetValue<string>() ?? "Int16";
        if (device is null || address is null || value is null)
        {
            await PublishResultAsync(device, false, "payload needs device, address and value").ConfigureAwait(false);
            return;
        }

        try
        {
            var client = _host.GetClient(device);
            var write = await WriteTypedAsync(client, address, value, type).ConfigureAwait(false);
            await PublishResultAsync(device, write.Success, write.Success ? "ok" : write.ToString()).ConfigureAwait(false);
        }
        catch (CommunicationException ex)
        {
            await PublishResultAsync(device, false, ex.Message).ConfigureAwait(false);
        }
    }

    private static Task<CommResult> WriteTypedAsync(IPlcClient client, string address, JsonNode value, string type) => type switch
    {
        "Bit" or "Bool" => client.WriteAsync(address, value.GetValue<bool>()),
        "Int16" => client.WriteAsync(address, value.GetValue<short>()),
        "UInt16" => client.WriteAsync(address, value.GetValue<ushort>()),
        "Int32" => client.WriteAsync(address, value.GetValue<int>()),
        "UInt32" => client.WriteAsync(address, value.GetValue<uint>()),
        "Int64" => client.WriteAsync(address, value.GetValue<long>()),
        "Float32" or "Float" => client.WriteAsync(address, value.GetValue<float>()),
        "Float64" or "Double" => client.WriteAsync(address, value.GetValue<double>()),
        "String" => client.WriteStringAsync(address, value.GetValue<string>(), checked((ushort)((value.GetValue<string>().Length + 1) / 2 + 1))),
        _ => Task.FromResult(CommResult.Fail(CommErrorKind.InvalidArgument, null, $"unknown type '{type}'")),
    };

    private static JsonObject ToJson(IReadOnlyDictionary<string, object?> changed)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in changed)
            obj[name] = value is null ? null : JsonSerializer.SerializeToNode(value, CommunicationConfigLoader.JsonOptions);
        return obj;
    }

    private async Task PublishResultAsync(string? device, bool success, string message)
    {
        var payload = new JsonObject
        {
            ["device"] = device,
            ["success"] = success,
            ["message"] = message,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await PublishAsync(ResultTopic, payload.ToJsonString()).ConfigureAwait(false);
    }

    private async Task PublishAsync(string topic, string payload)
    {
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT command failed: {Reason}")]
    private partial void LogCommandFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT publish failed: {Reason}")]
    private partial void LogPublishFailed(string reason);

    public async ValueTask DisposeAsync()
    {
        if (_attachedEngine is not null)
            _attachedEngine.PollCompleted -= OnPollCompleted;

        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync().ConfigureAwait(false);
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
