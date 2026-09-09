namespace IndustrialCommunication.Configuration;

/// <summary>Per-device runtime behaviour (defaults section merged with device-level overrides).</summary>
public sealed class DeviceRuntimeOptions
{
    public int TimeoutMs { get; set; } = 3000;
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int ConnectRetries { get; set; } = 2;
    public int RetryIntervalMs { get; set; } = 2000;
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Background connection monitor (disabled by default).</summary>
    public ConnectionMonitorConfig ConnectionMonitor { get; set; } = new();

    public static DeviceRuntimeOptions Default { get; } = new();
}
