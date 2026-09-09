namespace IndustrialCommunication;

/// <summary>
/// Driver-parsed address. Each driver maps its address string (e.g. <c>DB1.DBW10</c>, <c>D100</c>, <c>CIO100.5</c>)
/// onto this record; area names are driver-defined.
/// </summary>
public sealed record DeviceAddress
{
    /// <summary>Driver-defined area identifier, e.g. "HR", "DB", "D", "CIO".</summary>
    public required string Area { get; init; }

    /// <summary>Offset within the area: bit number for pure bit devices, byte offset for S7, word number for word devices.</summary>
    public required int Offset { get; init; }

    /// <summary>Bit number for <c>word.bit</c> style addresses (e.g. CIO100.5 → 5); otherwise 0.</summary>
    public int Bit { get; init; }

    /// <summary>True when the address targets a single bit.</summary>
    public bool IsBit { get; init; }

    /// <summary>S7 data block number; 0 for non-DB areas.</summary>
    public int DbNo { get; init; }

    /// <summary>The original address string as the caller wrote it (set by PlcClientBase; drivers with non-numeric addressing like NodeIds use it).</summary>
    public string? Raw { get; init; }

    public override string ToString() => DbNo > 0
        ? $"{Area}{DbNo}+{Offset}" + (IsBit ? $".{Bit}" : string.Empty)
        : $"{Area}{Offset}" + (IsBit ? $".{Bit}" : string.Empty);
}
