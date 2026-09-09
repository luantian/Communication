using IndustrialCommunication.Configuration;
using IndustrialCommunication.Mitsubishi;

namespace IndustrialCommunication;

public static class McDriverRegistration
{
    /// <summary>Registers the Mitsubishi MELSEC MC-protocol driver (3E/4E binary over TCP) for protocol name "MitsubishiMc".</summary>
    public static DriverRegistry AddMitsubishiMc(this DriverRegistry registry) =>
        registry.Register<McOptions>(
            ProtocolNames.MitsubishiMc,
            static (options, device, context) => new McProtocolClient(options, device, context));

    /// <summary>Registers the Mitsubishi MELSEC MC-protocol driver over UDP for protocol name "MitsubishiMcUdp".</summary>
    public static DriverRegistry AddMitsubishiMcUdp(this DriverRegistry registry) =>
        registry.Register<McOptions>(
            ProtocolNames.MitsubishiMcUdp,
            static (options, device, context) => new McUdpClient(options, device, context));
}
