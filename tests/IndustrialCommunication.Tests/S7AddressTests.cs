using IndustrialCommunication.Siemens;
using Xunit;

namespace IndustrialCommunication.Tests;

public class S7AddressTests
{
    [Theory]
    [InlineData("T5", "T", 0, 5, 0, false)]
    [InlineData("c10", "C", 0, 10, 0, false)]
    [InlineData("T0", "T", 0, 0, 0, false)]
    [InlineData("DB1.DBX10.0", "DB", 1, 10, 0, true)]
    [InlineData("DB20.DBW12", "DB", 20, 12, 0, false)]
    [InlineData("db3.dbd4", "DB", 3, 4, 0, false)]
    [InlineData("DB100.DBB7", "DB", 100, 7, 0, false)]
    [InlineData("DB1.DBX8.7", "DB", 1, 8, 7, true)]
    [InlineData("M100.5", "M", 0, 100, 5, true)]
    [InlineData("MW100", "M", 0, 100, 0, false)]
    [InlineData("MD200", "M", 0, 200, 0, false)]
    [InlineData("MB0", "M", 0, 0, 0, false)]
    [InlineData("I0.1", "I", 0, 0, 1, true)]
    [InlineData("E2.7", "I", 0, 2, 7, true)]
    [InlineData("IW64", "I", 0, 64, 0, false)]
    [InlineData("Q4.0", "Q", 0, 4, 0, true)]
    [InlineData("AW20", "Q", 0, 20, 0, false)]
    public void Parses_all_supported_forms(string input, string area, int dbNo, int offset, int bit, bool isBit)
    {
        var parsed = S7Address.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(dbNo, parsed.DbNo);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(bit, parsed.Bit);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("M100")]        // ambiguous: use M100.0 (bit) or MW100 (word)
    [InlineData("X10")]
    [InlineData("DB1.DBX10.9")] // bit out of range
    [InlineData("DB1.DBZ10")]   // unknown access type
    [InlineData("DB.DBW10")]    // missing DB number
    [InlineData("DB1.DBW")]
    [InlineData("DB1.DBW-2")]
    [InlineData("M100.5.6")]
    [InlineData("M100.9")]          // timers/counters not supported yet
    public void Invalid_addresses_throw_FormatException(string input)
    {
        Assert.Throws<FormatException>(() => S7Address.Parse(input));
    }

    [Fact]
    public void Ambiguous_message_guides_the_user()
    {
        var ex = Assert.Throws<FormatException>(() => S7Address.Parse("M100"));

        Assert.Contains("M100.5", ex.Message);
        Assert.Contains("MW100", ex.Message);
    }
}

public class S7OptionsTests
{
    [Theory]
    [InlineData("S7200")]
    [InlineData("s7200smart")]
    [InlineData("S7300")]
    [InlineData("S7400")]
    [InlineData("s71200")]
    [InlineData("S71500")]
    public void CpuType_resolves_known_families(string cpu)
    {
        var options = new S7Options { Ip = "192.168.1.10", CpuType = cpu };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Unknown_cpu_type_is_reported_with_the_list()
    {
        var options = new S7Options { Ip = "192.168.1.10", CpuType = "S72000" };

        var errors = options.Validate();

        var error = Assert.Single(errors);
        Assert.Contains("S71200", error);
    }

    [Fact]
    public void Ip_and_port_are_validated()
    {
        var options = new S7Options { Ip = "localhost-host", Port = 70000 };

        Assert.Equal(2, options.Validate().Count);
    }
}
