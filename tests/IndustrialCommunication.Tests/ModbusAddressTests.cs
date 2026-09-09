using IndustrialCommunication.Modbus;
using Xunit;

namespace IndustrialCommunication.Tests;

public class ModbusAddressTests
{
    [Theory]
    [InlineData("HR100", "HR", 100, false)]
    [InlineData("hr0", "HR", 0, false)]
    [InlineData("IR0", "IR", 0, false)]
    [InlineData("ir65535", "IR", 65535, false)]
    [InlineData("C10", "C", 10, true)]
    [InlineData("c0", "C", 0, true)]
    [InlineData("DR3", "DR", 3, true)]
    [InlineData("DI7", "DR", 7, true)]
    public void Named_areas_parse_case_insensitively(string input, string area, int offset, bool isBit)
    {
        var parsed = ModbusAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("40001", "HR", 0)]
    [InlineData("40101", "HR", 100)]
    [InlineData("30001", "IR", 0)]
    [InlineData("30303", "IR", 302)]
    [InlineData("00001", "C", 0)]
    [InlineData("00017", "C", 16)]
    [InlineData("10001", "DR", 0)]
    [InlineData("10005", "DR", 4)]
    public void Classic_5digit_addresses_decrement_by_one(string input, string area, int offset)
    {
        var parsed = ModbusAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
    }

    [Theory]
    [InlineData("400001", "HR", 0)]
    [InlineData("465536", "HR", 65535)]
    [InlineData("300001", "IR", 0)]
    [InlineData("000001", "C", 0)]
    [InlineData("100001", "DR", 0)]
    public void Classic_6digit_addresses_parse(string input, string area, int offset)
    {
        var parsed = ModbusAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XX10")]
    [InlineData("HR")]
    [InlineData("HRabc")]
    [InlineData("HR-1")]
    [InlineData("65536")]      // classic form with unknown area digit 6
    [InlineData("50001")]      // unknown area digit 5
    [InlineData("40000")]      // one below the first usable address
    [InlineData("465537")]     // offset beyond 65535
    [InlineData("1234567")]    // 7 digits
    [InlineData("12a34")]      // neither letters-first nor pure digits
    public void Invalid_addresses_throw_FormatException(string input)
    {
        Assert.Throws<FormatException>(() => ModbusAddress.Parse(input));
    }

    [Fact]
    public void Error_message_mentions_both_forms()
    {
        var ex = Assert.Throws<FormatException>(() => ModbusAddress.Parse("12a34"));

        Assert.Contains("HR100", ex.Message);
        Assert.Contains("40001", ex.Message);
    }
}
