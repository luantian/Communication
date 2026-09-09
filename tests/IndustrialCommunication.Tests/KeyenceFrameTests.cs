using IndustrialCommunication.Keyence;
using Xunit;

namespace IndustrialCommunication.Tests;

public class KeyenceFrameTests
{
    // Golden bytes cross-checked against the KV manual chapter 5 and real captures:
    // "RDS R100 4\r" → "1 0 1 0\r\n", "RD DM0\r" → "00011\r\n" (.U fixed width).
    [Fact]
    public void Read_commands_match_reference_format()
    {
        Assert.Equal("RDS DM100.U 2\r", KeyenceFrame.BuildReadWords("DM", 100, 2));
        Assert.Equal("RDS R105 4\r", KeyenceFrame.BuildReadBits("R", 105, 4));
        Assert.Equal("?E\r", KeyenceFrame.BuildErrorStatusProbe());
    }

    [Fact]
    public void Write_commands_match_reference_format()
    {
        Assert.Equal("WRS DM200.U 3 15025 65535 7\r", KeyenceFrame.BuildWriteWords("DM", 200, [15025, 65535, 7]));
        Assert.Equal("WRS R100 4 1 0 1 0\r", KeyenceFrame.BuildWriteBits("R", 100, [true, false, true, false]));
    }

    [Fact]
    public void Parse_words_and_bits_from_manual_examples()
    {
        Assert.Equal(new ushort[] { 11 }, KeyenceFrame.ParseWords("00011", 1));
        Assert.Equal(new ushort[] { 15025, 5400 }, KeyenceFrame.ParseWords("15025 05400", 2));
        Assert.Equal(new bool[] { true, false, true, false }, KeyenceFrame.ParseBits("1 0 1 0", 4));
    }

    [Fact]
    public void Error_replies_map_to_exception_with_code()
    {
        var ex = Assert.Throws<KeyenceErrorException>(() => KeyenceFrame.ParseTokens("E0"));

        Assert.Equal("E0", ex.Code);
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void Wrong_token_count_is_a_protocol_error()
    {
        Assert.Throws<InvalidDataException>(() => KeyenceFrame.ParseWords("1 2", 3));
        Assert.Throws<InvalidDataException>(() => KeyenceFrame.ParseBits("1 x", 2));
    }
}

public class KeyenceAddressTests
{
    [Theory]
    [InlineData("DM100", "DM", 100, false)]
    [InlineData("dm0", "DM", 0, false)]
    [InlineData("EM200", "EM", 200, false)]
    [InlineData("ZF100000", "ZF", 100000, false)]
    [InlineData("W1F", "W", 31, false)]
    [InlineData("R100", "R", 100, true)]
    [InlineData("MR1000", "MR", 1000, true)]
    [InlineData("LR50", "LR", 50, true)]
    [InlineData("B0A", "B", 10, true)]
    [InlineData("VB1F", "VB", 31, true)]
    public void Parses_word_and_bit_devices(string input, string area, int offset, bool isBit)
    {
        var parsed = KeyenceAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DM")]
    [InlineData("Q100")]
    [InlineData("DMabc")]
    [InlineData("DM-5")]
    public void Invalid_addresses_throw(string input)
    {
        Assert.Throws<FormatException>(() => KeyenceAddress.Parse(input));
    }

    [Fact]
    public void Hex_vs_decimal_devices()
    {
        Assert.True(KeyenceAddress.GetDevice("W").IsHex);
        Assert.True(KeyenceAddress.GetDevice("B").IsHex);
        Assert.False(KeyenceAddress.GetDevice("DM").IsHex);
        Assert.True(KeyenceAddress.GetDevice("DM").IsWord);
        Assert.False(KeyenceAddress.GetDevice("R").IsWord);
    }
}
