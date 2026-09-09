using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Omron;

public sealed class FinsOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 9600;

    /// <summary>FINS node address of this client (0..255). Convention: the last byte of the PC's IP address.</summary>
    public byte SourceNode { get; set; } = 0x19;

    /// <summary>FINS node address of the PLC. Default: the last byte of the PLC's IP address.</summary>
    public byte? DestNode { get; set; }

    public byte DestNet { get; set; }
    public byte SourceNet { get; set; }

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

    /// <summary>Effective destination node: configured value or the last byte of the PLC IP.</summary>
    public byte ResolveDestNode()
    {
        if (DestNode.HasValue)
            return DestNode.Value;
        if (IPAddress.TryParse(Ip, out var ip))
            return ip.GetAddressBytes()[^1];
        return 0;
    }
}
