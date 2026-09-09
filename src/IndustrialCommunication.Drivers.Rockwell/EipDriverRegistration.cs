using IndustrialCommunication.Configuration;
using IndustrialCommunication.Rockwell;

namespace IndustrialCommunication;

public static class EipDriverRegistration
{
    /// <summary>Registers the Allen-Bradley EtherNet/IP driver (ControlLogix/CompactLogix, tag based) for protocol name "RockwellEtherNetIp".</summary>
    public static DriverRegistry AddRockwellEtherNetIp(this DriverRegistry registry) =>
        registry.Register<EipOptions>(
            ProtocolNames.RockwellEtherNetIp,
            static (options, device, context) => new EipPlcClient(options, device, context));
}
