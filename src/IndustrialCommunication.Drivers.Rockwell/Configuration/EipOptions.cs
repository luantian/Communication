using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Rockwell;

public sealed class EipOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 44818;

    /// <summary>Target controller slot: 0 for CompactLogix/onboard Ethernet, the chassis slot for ControlLogix.</summary>
    public byte Slot { get; set; }

    public EipOptions()
    {
        // Logix transmits little-endian values (low word first) → CDAB in the unified model.
        DataLayout = DataLayout.CDAB;
    }

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Ip))
            errors.Add("ip is required.");
        else if (!IPAddress.TryParse(Ip, out _))
            errors.Add($"ip '{Ip}' is not a valid IP address.");
        if (Port is < 1 or > 65535)
            errors.Add($"port {Port} is out of range (1..65535).");
        return errors;
    }
}
