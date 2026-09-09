using IndustrialCommunication.Mqtt.Sparkplug;
using Xunit;

namespace IndustrialCommunication.Tests;

public class SparkplugProtoTests
{
    [Fact]
    public void Varint_encoding_of_seq_and_metric_fields()
    {
        // seq=3 (field 3, varint) → tag 0x18, value 0x03
        var payload = SparkplugProto.EncodePayload(null, [], seq: 3);
        Assert.Equal([0x18, 0x03], payload);

        // seq=300 → varint 0xAC 0x02
        var big = SparkplugProto.EncodePayload(null, [], seq: 300);
        Assert.Equal([0x18, 0xAC, 0x02], big);
    }

    [Fact]
    public void Metric_encodes_name_datatype_and_long_value()
    {
        var payload = SparkplugProto.EncodePayload(null,
            [new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, LongValue = 42 }],
            seq: 0);

        // metrics field 2 (LEN): name field 1 "bdSeq", datatype field 4 = 4 (Int64), long_value field 11 = 42
        Assert.Equal(0x12, payload[0]);                 // field 2, wire type 2
        var metricLength = payload[1];
        var metric = payload[2..(2 + metricLength)];
        Assert.Equal(0x0A, metric[0]);                  // name field 1
        Assert.Equal(5, metric[1]);                     // "bdSeq"
        var nameEnd = 2 + 5;
        Assert.Equal(0x20, metric[nameEnd]);            // datatype field 4, varint
        Assert.Equal(4, metric[nameEnd + 1]);           // Int64
        Assert.Equal(0x58, metric[nameEnd + 2]);        // long_value field 11, varint
        Assert.Equal(42, metric[nameEnd + 3]);
    }

    [Fact]
    public void Double_metric_uses_fixed64_field_13()
    {
        var payload = SparkplugProto.EncodePayload(null,
            [new SparkplugMetric { Name = "t", DataType = SparkplugDataType.Double, DoubleValue = 1.0 }],
            seq: 0);

        var metricLength = payload[1];
        Assert.Contains((byte)0x69, payload);            // tag: (13 << 3) | 1 = 0x69, fixed64
        var tail = payload[(2 + metricLength - 8)..];   // last 8 bytes = IEEE bits of 1.0, LE
        var bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(tail);
        Assert.Equal(1.0, BitConverter.UInt64BitsToDouble(bits));
    }

    [Fact]
    public void Roundtrip_decode_recovers_metrics_and_seq()
    {
        var original = new List<SparkplugMetric>
        {
            new() { Name = "speed", DataType = SparkplugDataType.Double, DoubleValue = 12.5 },
            new() { Name = "run", DataType = SparkplugDataType.Boolean, BooleanValue = true },
            new() { Name = "batch", DataType = SparkplugDataType.String, StringValue = "B-47" },
            new() { Name = "count", DataType = SparkplugDataType.Int64, LongValue = 987654321 },
        };
        var payload = SparkplugProto.EncodePayload(1_700_000_000_000, original, seq: 7);

        var (seq, metrics) = SparkplugProto.DecodePayload(payload);

        Assert.Equal(7u, seq);
        Assert.Equal(4, metrics.Count);
        Assert.Equal("speed", metrics[0].Name);
        Assert.Equal(12.5, metrics[0].DoubleValue);
        Assert.Equal("run", metrics[1].Name);
        Assert.True(metrics[1].BooleanValue);
        Assert.Equal("batch", metrics[2].Name);
        Assert.Equal("B-47", metrics[2].StringValue);
        Assert.Equal("count", metrics[3].Name);
        Assert.Equal(987654321L, metrics[3].LongValue);
    }

    [Fact]
    public void Decode_skips_unknown_fields()
    {
        // hand-build: field 6 (unknown varint) then seq field 3
        var payload = new byte[] { 0x30, 0x2A, 0x18, 0x05 }; // 6:42, 3:5

        var (seq, metrics) = SparkplugProto.DecodePayload(payload);

        Assert.Equal(5u, seq);
        Assert.Empty(metrics);
    }
}
