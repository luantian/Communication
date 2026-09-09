using System.Globalization;

namespace IndustrialCommunication.OpcUa;

/// <summary>Conversions between OPC UA node values (boxed .NET types) and the unified primitives/typed layer.</summary>
internal static class OpcUaValue
{
    public static bool TryConvert<T>(object? value, out T result) where T : struct
    {
        if (value is T typed)
        {
            result = typed;
            return true;
        }

        if (value is bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or string)
        {
            try
            {
                result = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
                return true;
            }
            catch (InvalidCastException)
            {
            }
            catch (FormatException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        result = default;
        return false;
    }

    public static CommResult<ushort[]> ToWords(object? value, int count)
    {
        ushort[] words = value switch
        {
            ushort[] array => array,
            short[] array => [.. array.Select(w => unchecked((ushort)w))],
            bool[] array => [.. array.Select(b => b ? (ushort)1 : (ushort)0)],
            bool b => [b ? (ushort)1 : (ushort)0],
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double
                => TryConvert<ushort>(value, out var word) ? [word] : [],
            _ => [],
        };

        if (words.Length < count)
            return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                $"Node value '{value?.GetType().Name ?? "null"}' does not carry {count} word(s).");

        return CommResult<ushort[]>.Ok(words[..count]);
    }

    public static CommResult<bool[]> ToBits(object? value, int count)
    {
        bool[] bits = value switch
        {
            bool[] array => array,
            bool b => [b],
            _ => [],
        };

        if (bits.Length < count)
            return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                $"Node value '{value?.GetType().Name ?? "null"}' does not carry {count} bit(s).");

        return CommResult<bool[]>.Ok(bits[..count]);
    }
}
