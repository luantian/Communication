namespace IndustrialCommunication;

/// <summary>PLC value type of a point. Determines the word count consumed and the .NET representation.</summary>
public enum PlcValueType
{
    Bit,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    Float32,
    Float64,
    String,
}

public static class ValueTypeMap
{
    public static PlcValueType FromClrType(Type type)
    {
        if (type == typeof(bool)) return PlcValueType.Bit;
        if (type == typeof(short)) return PlcValueType.Int16;
        if (type == typeof(ushort)) return PlcValueType.UInt16;
        if (type == typeof(int)) return PlcValueType.Int32;
        if (type == typeof(uint)) return PlcValueType.UInt32;
        if (type == typeof(long)) return PlcValueType.Int64;
        if (type == typeof(float)) return PlcValueType.Float32;
        if (type == typeof(double)) return PlcValueType.Float64;
        throw new NotSupportedException(
            $"Type {type.Name} is not supported. Use bool, short, ushort, int, uint, long, float or double (strings via ReadStringAsync/WriteStringAsync).");
    }

    public static int WordCount(PlcValueType type) => type switch
    {
        PlcValueType.Bit or PlcValueType.Int16 or PlcValueType.UInt16 => 1,
        PlcValueType.Int32 or PlcValueType.UInt32 or PlcValueType.Float32 => 2,
        PlcValueType.Int64 or PlcValueType.Float64 => 4,
        PlcValueType.String => throw new NotSupportedException("String word count must be given explicitly."),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static Type ClrType(PlcValueType type) => type switch
    {
        PlcValueType.Bit => typeof(bool),
        PlcValueType.Int16 => typeof(short),
        PlcValueType.UInt16 => typeof(ushort),
        PlcValueType.Int32 => typeof(int),
        PlcValueType.UInt32 => typeof(uint),
        PlcValueType.Int64 => typeof(long),
        PlcValueType.Float32 => typeof(float),
        PlcValueType.Float64 => typeof(double),
        PlcValueType.String => typeof(string),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
