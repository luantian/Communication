using IndustrialCommunication.Configuration;

namespace IndustrialCommunication;

/// <summary>
/// Explicit protocol → driver-factory registration. No reflection scanning:
/// applications compose exactly the drivers they reference.
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> RegisteredProtocols => _entries.Keys.ToArray();

    /// <summary>Registers a driver factory for a protocol name. Returns the registry for fluent composition.</summary>
    public DriverRegistry Register<TOptions>(
        string protocol,
        Func<TOptions, DeviceConfig, ClientBuildContext, IPlcClient> factory)
        where TOptions : ConnectionOptions
    {
        if (string.IsNullOrWhiteSpace(protocol))
            throw new ArgumentException("Protocol name must not be empty.", nameof(protocol));
        ArgumentNullException.ThrowIfNull(factory);

        if (_entries.ContainsKey(protocol))
            throw new CommunicationException($"Protocol '{protocol}' is already registered.");

        _entries[protocol] = new Entry(
            typeof(TOptions),
            (options, device, context) => factory((TOptions)options, device, context));
        return this;
    }

    internal bool TryGet(string protocol, out Entry entry)
    {
        if (_entries.TryGetValue(protocol, out var found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    internal Entry GetOrThrow(string protocol, string deviceName)
    {
        if (_entries.TryGetValue(protocol, out var entry))
            return entry;

        var known = _entries.Count == 0
            ? "none is registered"
            : string.Join(", ", _entries.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase));
        throw new CommunicationException(
            $"Device '{deviceName}' uses protocol '{protocol}' which is not registered (registered: {known}). " +
            "Add the driver package and the corresponding Add...() registration.");
    }

    internal sealed record Entry(
        Type OptionsType,
        Func<ConnectionOptions, DeviceConfig, ClientBuildContext, IPlcClient> Factory);
}
