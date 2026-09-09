using IndustrialCommunication.Configuration;
using IndustrialCommunication.Keyence;

namespace IndustrialCommunication;

public static class KeyenceDriverRegistration
{
    /// <summary>Registers the Keyence KV upper-computer-link driver (TCP, default port 8501) for protocol name "KeyenceUpperLink".</summary>
    public static DriverRegistry AddKeyenceUpperLink(this DriverRegistry registry) =>
        registry.Register<KeyenceOptions>(
            ProtocolNames.KeyenceUpperLink,
            static (options, device, context) => new KeyencePlcClient(options, device, context));

    /// <summary>Registers the Keyence KV upper-computer-link driver over UDP for protocol name "KeyenceUpperLinkUdp".</summary>
    public static DriverRegistry AddKeyenceUpperLinkUdp(this DriverRegistry registry) =>
        registry.Register<KeyenceOptions>(
            ProtocolNames.KeyenceUpperLinkUdp,
            static (options, device, context) => new KeyenceUdpClient(options, device, context));
}
