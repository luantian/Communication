using System.Buffers.Binary;
using System.Text;

namespace IndustrialCommunication.Mqtt.Sparkplug;

/// <summary>Sparkplug B data types (subset used by the bridge).</summary>
public enum SparkplugDataType : uint
{
    Unknown = 0,
    Int8 = 1,
    Int16 = 2,
    Int32 = 3,
    Int64 = 4,
    UInt8 = 5,
    UInt16 = 6,
    UInt32 = 7,
    UInt64 = 8,
    Float = 9,
    Double = 10,
    Boolean = 11,
    String = 12,
    DateTime = 13,
    Text = 14,
}

/// <summary>One Sparkplug metric: name, optional alias, timestamp, type and one scalar value.</summary>
public sealed record SparkplugMetric
{
    public required string Name { get; init; }
    public ulong Alias { get; init; }
    public ulong TimestampMs { get; init; }
    public SparkplugDataType DataType { get; init; }
    public bool IsNull { get; init; }

    public long LongValue { get; init; }
    public double DoubleValue { get; init; }
    public bool BooleanValue { get; init; }
    public string StringValue { get; init; } = string.Empty;
}

/// <summary>
/// Hand-written protobuf codec for the Sparkplug B payload subset the bridge emits/consumes
/// (field numbers verified against the official sparkplug_b.proto):
///   Payload: timestamp=1 (varint), metrics=2 (repeated), seq=3 (varint), uuid=4 (string), body=5 (bytes)
///   Metric:  name=1, alias=2, timestamp=3, datatype=4 (varint), is_null=7,
///            long_value=11 (varint), double_value=13 (fixed64), boolean_value=14, string_value=15
/// </summary>
public static class SparkplugProto
{
    public static byte[] EncodePayload(ulong? timestamp, IReadOnlyList<SparkplugMetric> metrics, ulong seq, string? uuid = null)
    {
        var stream = new MemoryStream();
        if (timestamp.HasValue)
            WriteVarintField(stream, 1, timestamp.Value);
        foreach (var metric in metrics)
            WriteMessageField(stream, 2, EncodeMetric(metric));
        WriteVarintField(stream, 3, seq);
        if (uuid is not null)
            WriteStringField(stream, 4, uuid);
        return stream.ToArray();
    }

    public static byte[] EncodeMetric(SparkplugMetric metric)
    {
        var stream = new MemoryStream();
        WriteStringField(stream, 1, metric.Name);
        if (metric.Alias != 0)
            WriteVarintField(stream, 2, metric.Alias);
        if (metric.TimestampMs != 0)
            WriteVarintField(stream, 3, metric.TimestampMs);
        if (metric.DataType != SparkplugDataType.Unknown)
            WriteVarintField(stream, 4, (uint)metric.DataType);
        if (metric.IsNull)
            WriteBoolField(stream, 7, true);

        switch (metric.DataType)
        {
            case SparkplugDataType.Float:
            case SparkplugDataType.Double:
                WriteFixed64Field(stream, 13, BitConverter.DoubleToUInt64Bits(metric.DoubleValue));
                break;
            case SparkplugDataType.Boolean:
                WriteBoolField(stream, 14, metric.BooleanValue);
                break;
            case SparkplugDataType.String:
            case SparkplugDataType.Text:
                WriteStringField(stream, 15, metric.StringValue);
                break;
            default:
                WriteVarintField(stream, 11, (ulong)metric.LongValue);
                break;
        }

        return stream.ToArray();
    }

    /// <summary>Decodes a payload into (seq, metrics) — the subset needed to read NCMD/DCMD commands.</summary>
    public static (ulong Seq, List<SparkplugMetric> Metrics) DecodePayload(ReadOnlySpan<byte> payload)
    {
        ulong seq = 0;
        var metrics = new List<SparkplugMetric>();

        var reader = new ProtoReader(payload);
        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    var metricSpan = reader.ReadLengthDelimited();
                    if (DecodeMetric(metricSpan) is { } metric)
                        metrics.Add(metric);
                    break;
                case 3:
                    seq = reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return (seq, metrics);
    }

    private static SparkplugMetric? DecodeMetric(ReadOnlySpan<byte> span)
    {
        string name = string.Empty;
        SparkplugDataType type = SparkplugDataType.Unknown;
        long longValue = 0;
        double doubleValue = 0;
        bool boolValue = false;
        string stringValue = string.Empty;

        var reader = new ProtoReader(span);
        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field)
            {
                case 1: name = Encoding.UTF8.GetString(reader.ReadLengthDelimited()); break;
                case 4: type = (SparkplugDataType)reader.ReadVarint(); break;
                case 11: longValue = (long)reader.ReadVarint(); break;
                case 13: doubleValue = BitConverter.UInt64BitsToDouble(reader.ReadFixed64()); break;
                case 14: boolValue = reader.ReadVarint() != 0; break;
                case 15: stringValue = Encoding.UTF8.GetString(reader.ReadLengthDelimited()); break;
                default: reader.Skip(wireType); break;
            }
        }

        return new SparkplugMetric
        {
            Name = name,
            DataType = type,
            LongValue = longValue,
            DoubleValue = doubleValue,
            BooleanValue = boolValue,
            StringValue = stringValue,
        };
    }

    private static void WriteVarintField(MemoryStream stream, int field, ulong value)
    {
        if (value == 0)
            return; // proto2 optional: omit default values
        WriteVarint(stream, ((ulong)field << 3) | 0);
        WriteVarint(stream, value);
    }

    private static void WriteStringField(MemoryStream stream, int field, string value)
    {
        if (value.Length == 0)
            return;
        WriteVarint(stream, ((ulong)field << 3) | 2);
        WriteVarint(stream, (ulong)Encoding.UTF8.GetByteCount(value));
        stream.Write(Encoding.UTF8.GetBytes(value));
    }

    private static void WriteMessageField(MemoryStream stream, int field, byte[] message)
    {
        WriteVarint(stream, ((ulong)field << 3) | 2);
        WriteVarint(stream, (ulong)message.Length);
        stream.Write(message);
    }

    private static void WriteFixed64Field(MemoryStream stream, int field, ulong value)
    {
        WriteVarint(stream, ((ulong)field << 3) | 1);
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBoolField(MemoryStream stream, int field, bool value)
    {
        if (!value)
            return;
        WriteVarint(stream, ((ulong)field << 3) | 0);
        stream.WriteByte(1);
    }

    private static void WriteVarint(MemoryStream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _position;

        public bool TryReadField(out int field, out int wireType)
        {
            field = 0;
            wireType = 0;
            if (_position >= _buffer.Length)
                return false;

            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wireType = (int)(tag & 0x7);
            return field > 0;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            int shift = 0;
            while (_position < _buffer.Length)
            {
                var b = _buffer[_position++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            return value;
        }

        public ulong ReadFixed64()
        {
            var slice = _buffer.Slice(_position, 8);
            _position += 8;
            return BinaryPrimitives.ReadUInt64LittleEndian(slice);
        }

        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            var length = (int)ReadVarint();
            var slice = _buffer.Slice(_position, length);
            _position += length;
            return slice;
        }

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarint(); break;
                case 1: _position += 8; break;
                case 2: ReadLengthDelimited(); break;
                case 5: _position += 4; break;
            }
        }
    }
}
