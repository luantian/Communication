using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.LsFEnet;

public class LsFEnetOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>FEnet dedicated-protocol TCP port (XGT server).</summary>
    public int Port { get; set; } = 2004;

    /// <summary>CPU info byte: XGK 0xA0, XGI 0xA4, XGR 0xA8, XGB(MK) 0xB0, XGB(IEC) 0xB4.</summary>
    public byte CpuInfo { get; set; } = 0xB0;

    public LsFEnetOptions()
    {
        // FEnet words travel little-endian (low word first) → CDAB in the unified model.
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
