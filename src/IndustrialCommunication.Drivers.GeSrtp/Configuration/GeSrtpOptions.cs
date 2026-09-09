using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.GeSrtp;

public class GeSrtpOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>SRTP TCP port; 18245 (0x4745 = "GE") on GE Ethernet modules.</summary>
    public int Port { get; set; } = 18245;

    public GeSrtpOptions()
    {
        // SRTP words are little-endian on the wire (low word first) → CDAB in the unified model.
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
