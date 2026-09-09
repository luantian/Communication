using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Panasonic;

public sealed class MewtocolOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>Mewtocol TCP port; Panasonic communication units default to 9094.</summary>
    public int Port { get; set; } = 9094;

    /// <summary>Station number 1..99 (two ASCII digits in every frame).</summary>
    public int Station { get; set; } = 1;

    public MewtocolOptions()
    {
        // Words travel low-byte-first within the 4-hex-digit ASCII payload → CDAB in the unified model.
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
        if (Station is < 1 or > 99)
            errors.Add($"station {Station} is out of range (1..99).");
        return errors;
    }
}
