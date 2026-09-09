namespace IndustrialCommunication;

/// <summary>A named point for group reads: address plus value type (and optional element count).</summary>
public sealed record DevicePoint
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public PlcValueType ValueType { get; init; } = PlcValueType.UInt16;

    /// <summary>Number of elements; 1 for scalars. Strings use this as the word length.</summary>
    public ushort ArrayLength { get; init; } = 1;
}
