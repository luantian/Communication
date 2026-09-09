using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Keyence;

public sealed class KeyenceOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>Upper-link TCP port; the KV-LE2x default is 8501 (8500 belongs to KV STUDIO).</summary>
    public int Port { get; set; } = 8501;

    public KeyenceOptions()
    {
        // 32-bit values span two DM words with the low word first (like MELSEC) → CDAB.
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
