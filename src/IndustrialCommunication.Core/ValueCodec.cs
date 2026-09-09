using System.Text;

namespace IndustrialCommunication;

/// <summary>
/// Encoding/decoding between 16-bit word buffers (big-endian words, as stored by PLCs) and .NET values.
/// All members are pure functions — safe to unit test and share across drivers.
/// </summary>
public static class ValueCodec
{
    /// <summary>Decodes <typeparamref name="T"/> from the first words of the buffer, honouring <paramref name="layout"/>.</summary>
    public static T Decode<T>(ReadOnlySpan<ushort> words, DataLayout layout) where T : struct
    {
        if (typeof(T) == typeof(bool)) { Require(words, 1); return (T)(object)(words[0] != 0); }
        if (typeof(T) == typeof(ushort)) { Require(words, 1); return (T)(object)words[0]; }
        if (typeof(T) == typeof(short)) { Require(words, 1); return (T)(object)unchecked((short)words[0]); }
        if (typeof(T) == typeof(int)) { Require(words, 2); return (T)(object)unchecked((int)Build32(words[0], words[1], layout)); }
        if (typeof(T) == typeof(uint)) { Require(words, 2); return (T)(object)Build32(words[0], words[1], layout); }
        if (typeof(T) == typeof(float)) { Require(words, 2); return (T)(object)BitConverter.UInt32BitsToSingle(Build32(words[0], words[1], layout)); }
        if (typeof(T) == typeof(long)) { Require(words, 4); return (T)(object)unchecked((long)Build64(words, layout)); }
        if (typeof(T) == typeof(double)) { Require(words, 4); return (T)(object)BitConverter.UInt64BitsToDouble(Build64(words, layout)); }
        throw new NotSupportedException($"Type {typeof(T).Name} is not supported by {nameof(ValueCodec)}.");
    }

    /// <summary>Encodes <paramref name="value"/> into words in the given <paramref name="layout"/>.</summary>
    public static ushort[] Encode<T>(T value, DataLayout layout) where T : struct
    {
        if (typeof(T) == typeof(bool)) return [(bool)(object)value ? (ushort)1 : (ushort)0];
        if (typeof(T) == typeof(ushort)) return [(ushort)(object)value];
        if (typeof(T) == typeof(short)) return [unchecked((ushort)(short)(object)value)];
        if (typeof(T) == typeof(int)) return Split32(unchecked((uint)(int)(object)value), layout);
        if (typeof(T) == typeof(uint)) return Split32((uint)(object)value, layout);
        if (typeof(T) == typeof(float)) return Split32(BitConverter.SingleToUInt32Bits((float)(object)value), layout);
        if (typeof(T) == typeof(long)) return Split64(unchecked((ulong)(long)(object)value), layout);
        if (typeof(T) == typeof(double)) return Split64(BitConverter.DoubleToUInt64Bits((double)(object)value), layout);
        throw new NotSupportedException($"Type {typeof(T).Name} is not supported by {nameof(ValueCodec)}.");
    }

    /// <summary>Boxed decode for a <see cref="PlcValueType"/>; used by group reads.</summary>
    public static object DecodeObject(PlcValueType type, ReadOnlySpan<ushort> words, DataLayout layout) => type switch
    {
        PlcValueType.Bit => words[0] != 0,
        PlcValueType.Int16 => unchecked((short)words[0]),
        PlcValueType.UInt16 => words[0],
        PlcValueType.Int32 => unchecked((int)Build32(words[0], words[1], layout)),
        PlcValueType.UInt32 => Build32(words[0], words[1], layout),
        PlcValueType.Float32 => BitConverter.UInt32BitsToSingle(Build32(words[0], words[1], layout)),
        PlcValueType.Int64 => unchecked((long)Build64(words, layout)),
        PlcValueType.Float64 => BitConverter.UInt64BitsToDouble(Build64(words, layout)),
        _ => throw new NotSupportedException($"{type} cannot be decoded without a word count."),
    };

    /// <summary>Decodes a string: unpacks big-endian words to bytes, trims trailing NUL padding, decodes with <paramref name="encoding"/>.</summary>
    public static string DecodeString(ReadOnlySpan<ushort> words, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        if (words.IsEmpty)
            return string.Empty;

        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
        {
            bytes[2 * i] = (byte)(words[i] >> 8);
            bytes[2 * i + 1] = (byte)words[i];
        }

        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
            end--;

        return encoding.GetString(bytes, 0, end);
    }

    /// <summary>Encodes a string into <paramref name="wordLength"/> words, NUL padded. Throws when the encoded bytes do not fit.</summary>
    public static ushort[] EncodeString(string value, ushort wordLength, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);
        if (wordLength == 0)
            throw new ArgumentOutOfRangeException(nameof(wordLength), "Word length must be at least 1.");

        var bytes = encoding.GetBytes(value);
        if (bytes.Length > wordLength * 2)
            throw new ArgumentException(
                $"String encodes to {bytes.Length} bytes and does not fit into {wordLength} words ({wordLength * 2} bytes).",
                nameof(value));

        var words = new ushort[wordLength];
        for (int i = 0; i < bytes.Length; i++)
        {
            if ((i & 1) == 0)
                words[i >> 1] |= (ushort)(bytes[i] << 8);
            else
                words[i >> 1] |= bytes[i];
        }

        return words;
    }

    private static void Require(ReadOnlySpan<ushort> words, int needed)
    {
        if (words.Length < needed)
            throw new ArgumentException($"Value needs {needed} words but only {words.Length} were read.");
    }

    /// <summary>Reorders the 4 stored bytes (big-endian word pairs) into logical big-endian order. All four permutations are involutions, so this works for encoding too.</summary>
    private static void Permute32(Span<byte> b, DataLayout layout)
    {
        switch (layout)
        {
            case DataLayout.ABCD:
                return;
            case DataLayout.DCBA:
                (b[0], b[3]) = (b[3], b[0]);
                (b[1], b[2]) = (b[2], b[1]);
                return;
            case DataLayout.BADC:
                (b[0], b[1]) = (b[1], b[0]);
                (b[2], b[3]) = (b[3], b[2]);
                return;
            case DataLayout.CDAB:
                (b[0], b[2]) = (b[2], b[0]);
                (b[1], b[3]) = (b[3], b[1]);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout), layout, null);
        }
    }

    private static uint Build32(ushort w0, ushort w1, DataLayout layout)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)(w0 >> 8);
        b[1] = (byte)w0;
        b[2] = (byte)(w1 >> 8);
        b[3] = (byte)w1;
        Permute32(b, layout);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static ushort[] Split32(uint value, DataLayout layout)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)(value >> 24);
        b[1] = (byte)(value >> 16);
        b[2] = (byte)(value >> 8);
        b[3] = (byte)value;
        Permute32(b, layout);
        return [(ushort)((b[0] << 8) | b[1]), (ushort)((b[2] << 8) | b[3])];
    }

    private static ulong Build64(ReadOnlySpan<ushort> words, DataLayout layout)
    {
        Require(words, 4);
        Span<byte> b = stackalloc byte[8];
        for (int i = 0; i < 4; i++)
        {
            b[2 * i] = (byte)(words[i] >> 8);
            b[2 * i + 1] = (byte)words[i];
        }
        Permute32(b[..4], layout);
        Permute32(b[4..], layout);
        ulong hi = ((ulong)b[0] << 24) | ((ulong)b[1] << 16) | ((ulong)b[2] << 8) | b[3];
        ulong lo = ((ulong)b[4] << 24) | ((ulong)b[5] << 16) | ((ulong)b[6] << 8) | b[7];
        return (hi << 32) | lo;
    }

    private static ushort[] Split64(ulong value, DataLayout layout)
    {
        Span<byte> b = stackalloc byte[8];
        ulong hi = value >> 32;
        ulong lo = value & 0xFFFF_FFFFu;
        b[0] = (byte)(hi >> 24); b[1] = (byte)(hi >> 16); b[2] = (byte)(hi >> 8); b[3] = (byte)hi;
        b[4] = (byte)(lo >> 24); b[5] = (byte)(lo >> 16); b[6] = (byte)(lo >> 8); b[7] = (byte)lo;
        Permute32(b[..4], layout);
        Permute32(b[4..], layout);
        return
        [
            (ushort)((b[0] << 8) | b[1]),
            (ushort)((b[2] << 8) | b[3]),
            (ushort)((b[4] << 8) | b[5]),
            (ushort)((b[6] << 8) | b[7]),
        ];
    }
}
