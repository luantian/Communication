namespace IndustrialCommunication;

/// <summary>
/// Byte/word order of multi-word values as stored in the device, relative to the big-endian logical
/// byte sequence A B C D (A = most significant byte).
/// </summary>
public enum DataLayout
{
    /// <summary>[A B][C D] — big-endian, the PLC convention (default).</summary>
    ABCD = 0,

    /// <summary>[C D][A B] — word-swapped big-endian, common in Modbus devices.</summary>
    CDAB,

    /// <summary>[B A][D C] — byte-swapped per 16-bit word.</summary>
    BADC,

    /// <summary>[D C][B A] — little-endian byte sequence.</summary>
    DCBA,
}
