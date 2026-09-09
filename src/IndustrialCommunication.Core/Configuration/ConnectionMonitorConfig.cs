namespace IndustrialCommunication.Configuration;

/// <summary>
/// Background connection monitor: periodically probes every enabled device and reconnects
/// disconnected ones, so half-open connections are detected even without traffic.
/// </summary>
public sealed class ConnectionMonitorConfig
{
    public bool Enabled { get; set; }
    public int IntervalMs { get; set; } = 5000;
}
