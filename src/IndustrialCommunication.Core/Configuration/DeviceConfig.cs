using System.Text.Json;

namespace IndustrialCommunication.Configuration;

/// <summary>One <c>devices[]</c> entry of the configuration file.</summary>
public sealed class DeviceConfig
{
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }

    /// <summary>Raw <c>connection</c> node; deserialized into the driver's options type on client construction.</summary>
    public JsonElement? Connection { get; set; }
}
