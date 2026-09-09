namespace IndustrialCommunication;

/// <summary>Well-known protocol identifiers used in the <c>protocol</c> field of the JSON configuration.</summary>
public static class ProtocolNames
{
    public const string ModbusTcp = "ModbusTcp";
    public const string ModbusRtu = "ModbusRtu";
    public const string ModbusUdp = "ModbusUdp";
    public const string ModbusAscii = "ModbusAscii";
    public const string SiemensS7 = "S7";
    public const string MitsubishiMc = "MitsubishiMc";
    public const string MitsubishiMcUdp = "MitsubishiMcUdp";
    public const string OmronFins = "OmronFins";
    public const string OmronFinsUdp = "OmronFinsUdp";
    public const string PanasonicMewtocol = "PanasonicMewtocol";
    public const string KeyenceUpperLink = "KeyenceUpperLink";
    public const string KeyenceUpperLinkUdp = "KeyenceUpperLinkUdp";
    public const string RockwellEtherNetIp = "RockwellEtherNetIp";
    public const string OpcUa = "OpcUa";
    public const string GeSrtp = "GeSrtp";
    public const string LsFEnet = "LsFEnet";
}
