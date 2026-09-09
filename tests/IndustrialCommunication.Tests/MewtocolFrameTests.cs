using IndustrialCommunication.Panasonic;
using Xunit;

namespace IndustrialCommunication.Tests;

public class MewtocolFrameTests
{
    // Golden frames cross-checked against the Panasonic manuals (WUME-MEWCP-03 / ACG-M0015-2):
    // "%01#RDD011050110757\r", "%01#RCSX000A6C\r", "%01#WCSY000A159\r", "%01#WDD00400004016400000053\r".
    [Fact]
    public void Read_words_frame_matches_manual_example()
    {
        Assert.Equal("%01#RDD011050110757\r", MewtocolFrame.BuildReadWords(1, 1105, 1107));
    }

    [Fact]
    public void Write_words_frame_matches_manual_example()
    {
        // DT400 = 100 (0x0064 → "6400"), DT401 = 0
        Assert.Equal("%01#WDD00400004016400000053\r", MewtocolFrame.BuildWriteWords(1, 400, 401, [100, 0]));
    }

    [Fact]
    public void Contact_frames_match_manual_examples()
    {
        Assert.Equal("%01#RCSX000A6C\r", MewtocolFrame.BuildReadContact(1, 'X', 0, 0xA));
        Assert.Equal("%01#WCSY000A159\r", MewtocolFrame.BuildWriteContact(1, 'Y', 0, 0xA, true));
    }

    [Fact]
    public void Response_parse_swaps_low_byte_first_words()
    {
        var (code, data) = MewtocolFrame.ParseResponse("%01$RD630044330A0062");

        Assert.Equal("RD", code);
        Assert.Equal(new ushort[] { 0x0063, 0x3344, 0x000A }, MewtocolFrame.ParseWords(data, 3));
    }

    [Fact]
    public void Error_reply_maps_to_exception_with_code()
    {
        var ex = Assert.Throws<MewtocolErrorException>(() => MewtocolFrame.ParseResponse("%01!4203"));

        Assert.Equal("42", ex.Code);
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void Bcc_mismatch_is_a_protocol_error()
    {
        Assert.Throws<InvalidDataException>(() => MewtocolFrame.ParseResponse("%01$RD630044330A0063"));
        Assert.Throws<InvalidDataException>(() => MewtocolFrame.ParseResponse("garbage"));
    }

    [Fact]
    public void WordsHex_emits_low_byte_first()
    {
        Assert.Equal("6300", MewtocolFrame.WordsHex(0x0063));
        Assert.Equal("4433", MewtocolFrame.WordsHex(0x3344));
    }

    [Fact]
    public void Continuation_request_skips_bcc()
    {
        Assert.Equal("%01**&\r", MewtocolFrame.BuildContinuationRequest(1));
        Assert.Equal("%05**&\r", MewtocolFrame.BuildContinuationRequest(5));
    }

    [Fact]
    public void Intermediate_frame_signals_has_more_and_strips_separator()
    {
        // A segment ending in '&' before its BCC: data "6300", separator, valid BCC over "%01$RD6300&"
        var line = "%01$RD6300&" + Bcc("%01$RD6300&");
        var (code, data) = MewtocolFrame.ParseResponse(line, out var hasMore);

        Assert.Equal("RD", code);
        Assert.Equal("6300", data);
        Assert.True(hasMore);

        // a final segment (no '&') reports hasMore = false
        var final = "%01$RD4433" + Bcc("%01$RD4433");
        var (_, finalData) = MewtocolFrame.ParseResponse(final, out var more);
        Assert.Equal("4433", finalData);
        Assert.False(more);
    }

    private static string Bcc(string body)
    {
        byte acc = 0;
        foreach (var b in System.Text.Encoding.ASCII.GetBytes(body))
            acc ^= b;
        return acc.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
    }
}

public class MewtocolAddressTests
{
    [Theory]
    [InlineData("DT100", "DT", 100, 0, false)]
    [InlineData("dt0", "DT", 0, 0, false)]
    [InlineData("FL500", "FL", 500, 0, false)]
    [InlineData("LD200", "LD", 200, 0, false)]
    [InlineData("R0101", "R", 10, 1, true)]
    [InlineData("R10.1", "R", 10, 1, true)]
    [InlineData("X000A", "X", 0, 10, true)]
    [InlineData("Y011F", "Y", 11, 15, true)]
    [InlineData("LR0203", "LR", 20, 3, true)]
    public void Parses_word_and_bit_devices(string input, string area, int offset, int bit, bool isBit)
    {
        var parsed = MewtocolAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(bit, parsed.Bit);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DT")]
    [InlineData("Q100")]
    [InlineData("DTabc")]
    [InlineData("DT100000")]   // > 5 digits
    [InlineData("R123")]       // bit form needs 4 digits or word.bit
    [InlineData("R10.16")]     // hex bit only goes to F
    public void Invalid_addresses_throw(string input)
    {
        Assert.Throws<FormatException>(() => MewtocolAddress.Parse(input));
    }

    [Fact]
    public void Device_area_codes_match_mewtocol_table()
    {
        Assert.Equal('D', MewtocolAddress.GetDevice("DT").AreaCode);
        Assert.Equal('F', MewtocolAddress.GetDevice("FL").AreaCode);
        Assert.Equal('L', MewtocolAddress.GetDevice("LD").AreaCode);
        Assert.Equal('X', MewtocolAddress.GetDevice("X").AreaCode);
        Assert.True(MewtocolAddress.GetDevice("DT").IsWord);
        Assert.False(MewtocolAddress.GetDevice("R").IsWord);
    }
}
