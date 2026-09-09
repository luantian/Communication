using System.Text;
using IndustrialCommunication;
using Xunit;

namespace IndustrialCommunication.Tests;

public class ValueCodecTests
{
    // 0x12345678: logical big-endian bytes A B C D = 12 34 56 78
    [Theory]
    [InlineData(DataLayout.ABCD, 0x1234, 0x5678)] // [12 34][56 78]
    [InlineData(DataLayout.CDAB, 0x5678, 0x1234)] // [56 78][12 34]
    [InlineData(DataLayout.BADC, 0x3412, 0x7856)] // [34 12][78 56]
    [InlineData(DataLayout.DCBA, 0x7856, 0x3412)] // [78 56][34 12]
    public void Int32_layouts_map_to_expected_words(DataLayout layout, ushort w0, ushort w1)
    {
        var words = ValueCodec.Encode(0x1234_5678, layout);

        Assert.Equal(new[] { w0, w1 }, words);
        Assert.Equal(0x1234_5678u, ValueCodec.Decode<uint>(words, layout));
    }

    [Fact]
    public void Float32_abcd_matches_ieee754_big_endian()
    {
        var words = ValueCodec.Encode(1.0f, DataLayout.ABCD);

        Assert.Equal(new ushort[] { 0x3F80, 0x0000 }, words);
        Assert.Equal(1.0f, ValueCodec.Decode<float>(words, DataLayout.ABCD));
    }

    [Fact]
    public void Float32_cdab_is_word_swapped()
    {
        var words = new ushort[] { 0x0000, 0x3F80 };

        Assert.Equal(1.0f, ValueCodec.Decode<float>(words, DataLayout.CDAB));
        Assert.Equal(words, ValueCodec.Encode(1.0f, DataLayout.CDAB));
    }

    [Theory]
    [InlineData(DataLayout.ABCD)]
    [InlineData(DataLayout.CDAB)]
    [InlineData(DataLayout.BADC)]
    [InlineData(DataLayout.DCBA)]
    public void Roundtrip_all_layouts_and_types(DataLayout layout)
    {
        Assert.Equal(-12345.678, ValueCodec.Decode<double>(ValueCodec.Encode(-12345.678, layout), layout), 5);
        Assert.Equal(-9876543210L, ValueCodec.Decode<long>(ValueCodec.Encode(-9876543210L, layout), layout));
        Assert.Equal(-42, ValueCodec.Decode<int>(ValueCodec.Encode(-42, layout), layout));
        Assert.Equal((ushort)0xCAFE, ValueCodec.Decode<ushort>(ValueCodec.Encode((ushort)0xCAFE, layout), layout));
        Assert.Equal((short)-2, ValueCodec.Decode<short>(ValueCodec.Encode((short)-2, layout), layout));
        Assert.True(ValueCodec.Decode<bool>(ValueCodec.Encode(true, layout), layout));
    }

    [Fact]
    public void Int64_abcd_is_big_endian_word_sequence()
    {
        var words = ValueCodec.Encode(0x1122_3344_5566_7788L, DataLayout.ABCD);

        Assert.Equal(new ushort[] { 0x1122, 0x3344, 0x5566, 0x7788 }, words);
    }

    [Fact]
    public void Decode_with_too_few_words_throws()
    {
        Assert.Throws<ArgumentException>(() => ValueCodec.Decode<int>(new ushort[] { 1 }, DataLayout.ABCD));
        Assert.Throws<ArgumentException>(() => ValueCodec.Decode<double>(new ushort[] { 1, 2, 3 }, DataLayout.ABCD));
    }

    [Fact]
    public void Unsupported_type_throws()
    {
        Assert.Throws<NotSupportedException>(() => ValueCodec.Decode<decimal>(new ushort[] { 1 }, DataLayout.ABCD));
    }

    [Fact]
    public void DecodeObject_matches_typed_decode()
    {
        var words = ValueCodec.Encode(3.14f, DataLayout.CDAB);

        Assert.Equal(3.14f, (float)ValueCodec.DecodeObject(PlcValueType.Float32, words, DataLayout.CDAB));
    }

    [Fact]
    public void String_roundtrip_trims_nul_padding()
    {
        var words = ValueCodec.EncodeString("PUMP-1", 10, Encoding.UTF8);

        Assert.Equal(10, words.Length);
        Assert.Equal("PUMP-1", ValueCodec.DecodeString(words, Encoding.UTF8));
    }

    [Fact]
    public void String_encode_throws_when_it_does_not_fit()
    {
        Assert.Throws<ArgumentException>(() => ValueCodec.EncodeString("12345", 2, Encoding.UTF8));
    }

    [Fact]
    public void String_supports_multibyte_encodings()
    {
        var words = ValueCodec.EncodeString("水泵", 4, Encoding.UTF8);

        Assert.Equal("水泵", ValueCodec.DecodeString(words, Encoding.UTF8));
    }

    [Fact]
    public void String_empty_and_exact_fit()
    {
        Assert.Equal(string.Empty, ValueCodec.DecodeString([], Encoding.UTF8));
        Assert.Equal("ABCD", ValueCodec.DecodeString(ValueCodec.EncodeString("ABCD", 2, Encoding.ASCII), Encoding.ASCII));
    }
}
