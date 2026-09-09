using IndustrialCommunication.Configuration;
using IndustrialCommunication.Omron;

namespace IndustrialCommunication;

public static class FinsDriverRegistration
{
    /// <summary>Registers the Omron FINS/TCP driver (CP / CJ / CJ2 / NJ series) for protocol name "OmronFins".</summary>
    public static DriverRegistry AddOmronFins(this DriverRegistry registry) =>
        registry.Register<FinsOptions>(
            ProtocolNames.OmronFins,
            static (options, device, context) => new FinsTcpClient(options, device, context));

    /// <summary>Registers the Omron FINS/UDP driver for protocol name "OmronFinsUdp".</summary>
    public static DriverRegistry AddOmronFinsUdp(this DriverRegistry registry) =>
        registry.Register<FinsOptions>(
            ProtocolNames.OmronFinsUdp,
            static (options, device, context) => new FinsUdpClient(options, device, context));
}
