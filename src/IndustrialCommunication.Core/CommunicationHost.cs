using System.Text.Json;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialCommunication;

/// <summary>
/// Loads a configuration file, creates clients lazily by device name and disposes them together.
/// Device names are matched case-insensitively.
/// </summary>
public sealed class CommunicationHost : IAsyncDisposable, IDisposable
{
    private readonly CommunicationConfig _config;
    private readonly DriverRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, IPlcClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPlcClient> _created = [];
    private readonly object _gate = new();
    private ConnectionMonitor? _monitor;
    private bool _disposed;

    private CommunicationHost(CommunicationConfig config, DriverRegistry registry, ILoggerFactory loggerFactory)
    {
        _config = config;
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The loaded configuration (read-only usage: poll groups, defaults, devices).</summary>
    public CommunicationConfig Config => _config;

    /// <summary>Creates a polling engine over this host's poll groups.</summary>
    public PollingEngine CreatePollingEngine() => new(this, _loggerFactory);

    /// <summary>The raw <c>mqtt</c> configuration node, if present (consumed by the Mqtt bridge package).</summary>
    public System.Text.Json.JsonElement? MqttNode => _config.Mqtt;

    /// <summary>Starts the background connection monitor (also auto-started when defaults.connectionMonitor.enabled is true).</summary>
    public void StartConnectionMonitor()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _monitor ??= new ConnectionMonitor(this, _config.Defaults.ConnectionMonitor, _loggerFactory);
            _monitor.Start();
        }
    }

    public static CommunicationHost Load(
        string jsonPath,
        Action<DriverRegistry> configureDrivers,
        ILoggerFactory? loggerFactory = null)
    {
        var config = CommunicationConfigLoader.Load(jsonPath);
        return FromConfig(config, configureDrivers, loggerFactory);
    }

    public static CommunicationHost FromConfig(
        CommunicationConfig config,
        Action<DriverRegistry> configureDrivers,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(configureDrivers);

        var registry = new DriverRegistry();
        configureDrivers(registry);
        var host = new CommunicationHost(config, registry, loggerFactory ?? NullLoggerFactory.Instance);
        if (config.Defaults.ConnectionMonitor.Enabled)
            host.StartConnectionMonitor();
        return host;
    }

    /// <summary>Names of the enabled devices in configuration order.</summary>
    public IReadOnlyList<string> DeviceNames =>
        _config.Devices.Where(static d => d.Enabled).Select(static d => d.Name).ToArray();

    /// <summary>Returns the client for a device, creating it on first use (startup errors throw here).</summary>
    public IPlcClient GetClient(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name must not be empty.", nameof(deviceName));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_clients.TryGetValue(deviceName, out var existing))
                return existing;

            var device = _config.Devices.FirstOrDefault(
                d => d.Enabled && string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                ?? throw NotFound(deviceName);

            var entry = _registry.GetOrThrow(device.Protocol, device.Name);

            var options = DeserializeOptions(device, entry.OptionsType);
            var errors = options.Validate();
            if (errors.Count > 0)
                throw new CommunicationException(
                    $"Device '{device.Name}' has an invalid connection section: {string.Join("; ", errors)}");

            var context = new ClientBuildContext
            {
                Runtime = _config.Defaults,
                LoggerFactory = _loggerFactory,
            };

            var client = entry.Factory(options, device, context);
            _clients[device.Name] = client;
            _created.Add(client);
            return client;
        }
    }

    /// <summary>Connects every enabled device sequentially; each device reports its own outcome.</summary>
    public async Task<IReadOnlyDictionary<string, CommResult>> ConnectAllAsync(CancellationToken ct = default)
    {
        var results = new Dictionary<string, CommResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in DeviceNames)
        {
            try
            {
                await GetClient(name).ConnectAsync(ct).ConfigureAwait(false);
                results[name] = CommResult.Ok();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CommunicationException ex)
            {
                results[name] = CommResult.Fail(ex.Kind, ex.ErrorCode, ex.Message);
            }
        }

        return results;
    }

    /// <summary>Sync disposal bridge for containers/using statements; prefer <c>await using</c>.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        // Stop the monitor BEFORE marking this host disposed: the monitor loop drains with
        // GetClient calls that must still observe a live host (the loop additionally tolerates
        // ObjectDisposedException for the residual window).
        ConnectionMonitor? monitor;
        lock (_gate)
        {
            if (_disposed)
                return;
            monitor = _monitor;
            _monitor = null;
        }

        if (monitor is not null)
            await monitor.DisposeAsync().ConfigureAwait(false);

        List<IPlcClient> toDispose;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = [.. _created];
            _created.Clear();
            _clients.Clear();
        }

        foreach (var client in toDispose)
            await client.DisposeAsync().ConfigureAwait(false);
    }

    private CommunicationException NotFound(string deviceName)
    {
        var disabled = _config.Devices.FirstOrDefault(
            d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        if (disabled is { Enabled: false })
            throw new CommunicationException($"Device '{disabled.Name}' is disabled (enabled = false).");

        var available = DeviceNames.Count == 0
            ? "none configured"
            : string.Join(", ", DeviceNames);
        return new CommunicationException($"Unknown device '{deviceName}' (configured: {available}).");
    }

    private static ConnectionOptions DeserializeOptions(DeviceConfig device, Type optionsType)
    {
        // A missing connection node falls back to default options; required fields
        // (ip, port, ...) are then caught by Validate() with a precise message.
        if (device.Connection is not { ValueKind: JsonValueKind.Object } node)
            return CreateDefaultOptions(optionsType, device);

        try
        {
            var options = (ConnectionOptions?)JsonSerializer.Deserialize(node.GetRawText(), optionsType,
                CommunicationConfigLoader.JsonOptions);
            return options ?? throw new CommunicationException(
                $"Device '{device.Name}': connection could not be deserialized to {optionsType.Name}.");
        }
        catch (JsonException ex)
        {
            throw new CommunicationException(
                $"Device '{device.Name}' has an invalid connection section: {ex.Message}");
        }
    }

    private static ConnectionOptions CreateDefaultOptions(Type optionsType, DeviceConfig device)
    {
        try
        {
            return (ConnectionOptions)Activator.CreateInstance(optionsType)!;
        }
        catch (MissingMethodException)
        {
            throw new CommunicationException(
                $"Device '{device.Name}' is missing the 'connection' object and {optionsType.Name} has no default constructor.");
        }
    }
}
