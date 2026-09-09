using IndustrialCommunication.GeSrtp;
using Xunit;

namespace IndustrialCommunication.Tests;

public class GeSrtpFrameTests
{
    // Golden bytes from the reverse-engineered specification (Denton et al. 2017 + uGESRTP/jGESRTP).
    [Fact]
    public void Short_read_frame_matches_reference_bytes()
    {
        // Read %R100, 10 words (offset = 100-1 = 99 = 0x0063 LE)
        var frame = GeSrtpFrame.BuildShortRead(sequence: 0x01, selector: 0x08, address: 100, units: 10);

        Assert.Equal(56, frame.Length);
        Assert.Equal(0x02, frame[0]);
        Assert.Equal(0x01, frame[2]);
        Assert.Equal(0x01, frame[9]);
        Assert.Equal(0x01, frame[17]);
        Assert.Equal(0x01, frame[30]);       // sequence repeat
        Assert.Equal(0xC0, frame[31]);       // short message
        Assert.Equal(0x10, frame[36]);       // mailbox dest 3600
        Assert.Equal(0x0E, frame[37]);
        Assert.Equal(0x01, frame[40]);
        Assert.Equal(0x01, frame[41]);
        Assert.Equal(0x04, frame[42]);       // read system memory
        Assert.Equal(0x08, frame[43]);       // %R word selector
        Assert.Equal(0x63, frame[44]);       // 99 LSB
        Assert.Equal(0x00, frame[45]);
        Assert.Equal(0x0A, frame[46]);       // 10 words
        Assert.Equal(0x00, frame[47]);
    }

    [Fact]
    public void Short_write_frame_matches_paper_example()
    {
        // Write %R39 = 57 (0x39) — paper Fig.3 instance
        var frame = GeSrtpFrame.BuildShortWrite(sequence: 0x00, selector: 0x08, address: 39, units: 1, [0x39, 0x00]);

        Assert.Equal(0x07, frame[42]);       // write system memory
        Assert.Equal(0x26, frame[44]);       // 38 = 39-1
        Assert.Equal(0x00, frame[45]);
        Assert.Equal(0x01, frame[46]);
        Assert.Equal(0x00, frame[47]);
        Assert.Equal(0x39, frame[48]);       // 57 LE
        Assert.Equal(0x00, frame[49]);
    }

    [Fact]
    public void Extended_write_carries_data_after_the_header()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var frame = GeSrtpFrame.BuildExtendedWrite(sequence: 0x01, selector: 0x08, address: 100, units: 4, data);

        Assert.Equal(56 + 8, frame.Length);
        Assert.Equal(0x80, frame[31]);       // extended message
        Assert.Equal(8, frame[4]);           // text length = 8 bytes LE
        Assert.Equal(0x07, frame[50]);       // write service in extended position
        Assert.Equal(0x08, frame[51]);
        Assert.Equal(0x63, frame[52]);       // offset 99
        Assert.Equal(0x04, frame[54]);       // 4 words
        Assert.Equal(data, frame[56..]);
    }

    [Fact]
    public void Init_roundtrip()
    {
        Assert.Equal(new byte[56], GeSrtpFrame.BuildInitFrame());

        var ok = new byte[56];
        ok[0] = 0x01;
        Assert.Equal((byte)0x01, GeSrtpFrame.ParseInitResponse(ok));

        var rejected = new byte[56];
        rejected[0] = 0x02;
        Assert.Throws<InvalidDataException>(() => GeSrtpFrame.ParseInitResponse(rejected));
    }

    [Fact]
    public void Inline_and_buffered_responses_parse()
    {
        var inline = BuildResponse(0xD4, seq: 0x05);
        inline[44] = 0x34;
        inline[45] = 0x12;
        var parsed = GeSrtpFrame.ParseResponse(inline, expectedSequence: 0x05);
        Assert.Equal(GeSrtpFrame.ResponseKind.Inline, parsed.Kind);
        Assert.Equal(6, parsed.Data.Length);
        Assert.Equal(0x34, parsed.Data.ToArray()[0]);

        var buffered = BuildResponse(0x94, seq: 0x06);
        buffered[4] = 0x14;                   // 20 data bytes
        buffered[5] = 0x00;
        var parsedBuffered = GeSrtpFrame.ParseResponse(buffered, expectedSequence: 0x06);
        Assert.Equal(GeSrtpFrame.ResponseKind.WithBuffer, parsedBuffered.Kind);
        Assert.Equal(20, parsedBuffered.BufferedBytes);

        var error = BuildResponse(0xD1, seq: 0x07);
        error[42] = 0x01;                     // illegal service request
        var ex = Assert.Throws<GeSrtpStatusException>(() => GeSrtpFrame.ParseResponse(error, 0x07));
        Assert.Equal((byte)0x01, ex.Primary);

        // sequence mismatch is a protocol error
        Assert.Throws<InvalidDataException>(() => GeSrtpFrame.ParseResponse(inline, expectedSequence: 0x09));
    }

    [Fact]
    public void Words_are_little_endian()
    {
        Assert.Equal(new byte[] { 0x34, 0x12 }, GeSrtpFrame.WordsToLeBytes([0x1234]));
        Assert.Equal(new ushort[] { 0x1234 }, GeSrtpFrame.WordsFromLeBytes([0x34, 0x12], 1));
    }

    private static byte[] BuildResponse(byte messageType, byte seq)
    {
        var frame = new byte[56];
        frame[0] = 0x03;        // response type
        frame[2] = seq;         // echoed sequence
        frame[30] = seq;
        frame[31] = messageType;
        frame[36] = 0x20;       // mailbox dest 23072
        frame[37] = 0x5A;
        frame[40] = 0x01;
        frame[41] = 0x01;
        return frame;
    }
}

public class GeSrtpAddressTests
{
    [Theory]
    [InlineData("R100", "R", 100, false)]
    [InlineData("%R1", "R", 1, false)]
    [InlineData("ai10", "AI", 10, false)]
    [InlineData("AQ20", "AQ", 20, false)]
    [InlineData("I10", "I", 10, true)]
    [InlineData("%Q20", "Q", 20, true)]
    [InlineData("M100", "M", 100, true)]
    [InlineData("T5", "T", 5, true)]
    [InlineData("SA10", "SA", 10, true)]
    [InlineData("SB10", "SB", 10, true)]
    [InlineData("G1", "G", 1, true)]
    public void Parses_word_and_bit_memories(string input, string area, int offset, bool isBit)
    {
        var parsed = GeSrtpAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("R")]
    [InlineData("X100")]
    [InlineData("Rabc")]
    [InlineData("R0")]       // GE addresses are 1-based
    [InlineData("R-5")]
    public void Invalid_addresses_throw(string input)
    {
        Assert.Throws<FormatException>(() => GeSrtpAddress.Parse(input));
    }

    [Fact]
    public void Memory_selectors_match_reference_table()
    {
        Assert.Equal(0x08, GeSrtpAddress.GetMemory("R").Selector);
        Assert.Equal(0x0A, GeSrtpAddress.GetMemory("AI").Selector);
        Assert.Equal(0x0C, GeSrtpAddress.GetMemory("AQ").Selector);
        Assert.Equal(0x46, GeSrtpAddress.GetMemory("I").Selector);
        Assert.Equal(0x48, GeSrtpAddress.GetMemory("Q").Selector);
        Assert.Equal(0x4C, GeSrtpAddress.GetMemory("M").Selector);
        Assert.True(GeSrtpAddress.GetMemory("R").IsWord);
        Assert.False(GeSrtpAddress.GetMemory("M").IsWord);
    }
}
