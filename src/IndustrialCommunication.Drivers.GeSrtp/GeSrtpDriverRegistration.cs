using IndustrialCommunication.Configuration;
using IndustrialCommunication.GeSrtp;

namespace IndustrialCommunication;

public static class GeSrtpDriverRegistration
{
    /// <summary>Registers the GE SRTP driver (Series 90-30 / 90-70 / RX3i, TCP 18245) for protocol name "GeSrtp".</summary>
    public static DriverRegistry AddGeSrtp(this DriverRegistry registry) =>
        registry.Register<GeSrtpOptions>(
            ProtocolNames.GeSrtp,
            static (options, device, context) => new GeSrtpClient(options, device, context));
}
