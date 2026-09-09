namespace IndustrialCommunication.Configuration;

/// <summary>One <c>pollGroups[]</c> entry: a device, an interval and the points to read each cycle.</summary>
public sealed class PollGroupConfig
{
    public string Name { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public int IntervalMs { get; set; } = 1000;
    public List<DevicePoint> Points { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("poll group needs a name.");
        if (string.IsNullOrWhiteSpace(Device))
            errors.Add($"Poll group '{Name}' needs a device name.");
        if (IntervalMs < 10)
            errors.Add($"Poll group '{Name}': intervalMs must be at least 10.");
        if (Points.Count == 0)
            errors.Add($"Poll group '{Name}' has no points.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in Points)
        {
            if (string.IsNullOrWhiteSpace(point.Name))
                errors.Add($"Poll group '{Name}' has a point without a name.");
            else if (!seen.Add(point.Name))
                errors.Add($"Poll group '{Name}' has a duplicate point name '{point.Name}'.");
        }
        return errors;
    }
}
