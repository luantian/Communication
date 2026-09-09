namespace IndustrialCommunication;

/// <summary>
/// Per-point results of a group read. Every point carries its own
/// <see cref="CommResult"/> — a single bad address does not fail the group.
/// </summary>
public sealed class GroupReadResult
{
    private readonly Dictionary<string, object?> _values;

    internal GroupReadResult(Dictionary<string, object?> values, Dictionary<string, CommResult> statuses)
    {
        _values = values;
        Statuses = statuses;
    }

    public IReadOnlyDictionary<string, CommResult> Statuses { get; }

    public bool AllSuccess => Statuses.Values.All(static r => r.Success);

    /// <summary>Raw boxed value: bool/short/ushort/int/uint/long/float/double/string, or an array of these when the point declared ArrayLength &gt; 1. null on failure.</summary>
    public bool TryGetValue(string pointName, out object? value) => _values.TryGetValue(pointName, out value);

    /// <summary>Typed scalar access; throws when the point failed or the type does not match.</summary>
    public T GetValue<T>(string pointName) where T : struct
    {
        var value = GetExisting(pointName);
        if (value is T typed)
            return typed;
        throw new InvalidCastException($"Point '{pointName}' holds '{value?.GetType().Name ?? "null"}', not {typeof(T).Name}.");
    }

    /// <summary>Typed array access for points with ArrayLength &gt; 1.</summary>
    public T[] GetArray<T>(string pointName) where T : struct
    {
        var value = GetExisting(pointName);
        if (value is T[] typed)
            return typed;
        throw new InvalidCastException($"Point '{pointName}' holds '{value?.GetType().Name ?? "null"}', not {typeof(T).Name}[].");
    }

    private object? GetExisting(string pointName)
    {
        if (!Statuses.TryGetValue(pointName, out var status))
            throw new KeyNotFoundException($"Unknown point '{pointName}'.");
        status.EnsureSuccess();
        return _values[pointName];
    }
}
