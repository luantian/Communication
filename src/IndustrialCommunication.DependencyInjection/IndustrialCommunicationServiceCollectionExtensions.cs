using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.DependencyInjection;

/// <summary>Resolves device clients by name from the DI-registered <see cref="CommunicationHost"/>.</summary>
public interface IPlcClientFactory
{
    IPlcClient GetClient(string deviceName);

    /// <summary>Names of the enabled devices in configuration order.</summary>
    IReadOnlyList<string> DeviceNames { get; }
}

internal sealed class CommunicationHostPlcClientFactory(CommunicationHost host) : IPlcClientFactory
{
    public IPlcClient GetClient(string deviceName) => host.GetClient(deviceName);

    public IReadOnlyList<string> DeviceNames => host.DeviceNames;
}

public static class IndustrialCommunicationServiceCollectionExtensions
{
    /// <summary>Registers a singleton <see cref="CommunicationHost"/> loaded from a JSON file, plus <see cref="IPlcClientFactory"/>.</summary>
    public static IServiceCollection AddIndustrialCommunication(
        this IServiceCollection services,
        string configPath,
        Action<DriverRegistry> configureDrivers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDrivers);

        return services.AddSingleton(sp =>
                CommunicationHost.Load(configPath, configureDrivers, sp.GetService<ILoggerFactory>()))
            .AddSingleton<IPlcClientFactory>(sp =>
                new CommunicationHostPlcClientFactory(sp.GetRequiredService<CommunicationHost>()));
    }

    /// <summary>Registers a singleton <see cref="CommunicationHost"/> from an already-loaded configuration, plus <see cref="IPlcClientFactory"/>.</summary>
    public static IServiceCollection AddIndustrialCommunication(
        this IServiceCollection services,
        CommunicationConfig config,
        Action<DriverRegistry> configureDrivers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(configureDrivers);

        return services.AddSingleton(sp =>
                CommunicationHost.FromConfig(config, configureDrivers, sp.GetService<ILoggerFactory>()))
            .AddSingleton<IPlcClientFactory>(sp =>
                new CommunicationHostPlcClientFactory(sp.GetRequiredService<CommunicationHost>()));
    }
}
