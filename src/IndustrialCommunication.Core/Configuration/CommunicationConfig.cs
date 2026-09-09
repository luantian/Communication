namespace IndustrialCommunication.Configuration;

/// <summary>Root of the communication configuration file: <c>defaults</c> plus the <c>devices</c> list.</summary>
public sealed class CommunicationConfig
{
    /// <summary>Runtime defaults applied to every device (timeouts, retries, auto-reconnect).</summary>
    public DeviceRuntimeOptions Defaults { get; set; } = new();

    public List<DeviceConfig> Devices { get; set; } = [];

    /// <summary>Polling groups driven by <see cref="PollingEngine"/>.</summary>
    public List<PollGroupConfig> PollGroups { get; set; } = [];

    /// <summary>Raw <c>mqtt</c> node; consumed by the IndustrialCommunication.Mqtt bridge package.</summary>
    public System.Text.Json.JsonElement? Mqtt { get; set; }

    /// <summary>Devices that will be created; disabled entries are filtered out. Duplicate/empty names are rejected.</summary>
    public static CommunicationConfig ValidateOrThrow(CommunicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in config.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Name))
                throw new CommunicationException("Every device needs a non-empty 'name'.");
            if (string.IsNullOrWhiteSpace(device.Protocol))
                throw new CommunicationException($"Device '{device.Name}' needs a non-empty 'protocol'.");
            if (!seen.Add(device.Name))
                throw new CommunicationException($"Duplicate device name '{device.Name}' (names are case-insensitive).");
        }

        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in config.PollGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                throw new CommunicationException("Every poll group needs a non-empty 'name'.");
            if (!groups.Add(group.Name))
                throw new CommunicationException($"Duplicate poll group name '{group.Name}' (names are case-insensitive).");
        }

        return config;
    }
}
