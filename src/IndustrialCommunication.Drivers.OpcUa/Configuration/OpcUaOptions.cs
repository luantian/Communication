using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.OpcUa;

public sealed class OpcUaOptions : ConnectionOptions
{
    /// <summary>Server endpoint, e.g. "opc.tcp://192.168.1.50:4840".</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>Optional user name; anonymous login when empty.</summary>
    public string? Username { get; set; }

    /// <summary>Optional password (stored in plain text — protect the config file accordingly).</summary>
    public string? Password { get; set; }

    /// <summary>false selects a SecurityPolicy.None endpoint when available (default); true prefers encrypted endpoints (requires a trusted application certificate).</summary>
    public bool UseSecurity { get; set; }

    public int SessionTimeoutMs { get; set; } = 60_000;

    public int KeepAliveIntervalMs { get; set; } = 5000;

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(EndpointUrl))
            errors.Add("endpointUrl is required (e.g. \"opc.tcp://192.168.1.50:4840\").");
        else if (!EndpointUrl.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
            errors.Add($"endpointUrl '{EndpointUrl}' must start with opc.tcp://");
        if (SessionTimeoutMs < 1000)
            errors.Add("sessionTimeoutMs must be at least 1000.");
        if (KeepAliveIntervalMs < 250)
            errors.Add("keepAliveIntervalMs must be at least 250.");
        return errors;
    }
}
