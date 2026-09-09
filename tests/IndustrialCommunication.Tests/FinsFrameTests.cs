using IndustrialCommunication.Omron;
using Xunit;

namespace IndustrialCommunication.Tests;

public class FinsFrameTests
{
    [Fact]
    public void Node_handshake_matches_reference_bytes()
    {
        var packet = FinsFrame.BuildNodeHandshake(0x19);

        Assert.Equal(
        [
            0x46, 0x49, 0x4E, 0x53,             // "FINS"
            0x00, 0x00, 0x00, 0x0C,             // length = 12
            0x00, 0x00, 0x00, 0x00,             // command: node address data send
            0x00, 0x00, 0x00, 0x00,             // error
            0x00, 0x00, 0x00, 0x19,             // client node 25
        ], packet);
    }

    [Fact]
    public void Handshake_response_parsing()
    {
        var response = new byte[]
        {
            0x46, 0x49, 0x4E, 0x53,
            0x00, 0x00, 0x00, 0x10,             // length = 16
            0x00, 0x00, 0x00, 0x01,             // command: reply
            0x00, 0x00, 0x00, 0x00,             // no error
            0x00, 0x00, 0x00, 0x19,             // node assigned to us (echoed)
            0x00, 0x00, 0x00, 0x0A,             // server node 10
        };

        var (localNode, serverNode) = FinsFrame.ParseNodeHandshakeResponse(response);
        Assert.Equal((byte)0x19, localNode);
        Assert.Equal((byte)0x0A, serverNode);

        var rejected = (byte[])response.Clone();
        rejected[15] = 0x02; // error field (bytes 12..15) = 2 → reject
        Assert.Throws<InvalidDataException>(() => FinsFrame.ParseNodeHandshakeResponse(rejected));

        var badMagic = (byte[])response.Clone();
        badMagic[0] = 0x58;
        Assert.Throws<InvalidDataException>(() => FinsFrame.ParseNodeHandshakeResponse(badMagic));
    }

    [Fact]
    public void Memory_read_command_frame_layout()
    {
        // Read DM100, 2 words: FINS header + 0101 + area 82 + word 0064 + bit 00 + count 0002
        var frame = FinsFrame.BuildFinsCommand(
            sourceNet: 0x00, sourceNode: 0x19, destNet: 0x00, destNode: 0x0A,
            sid: 0x07, command: FinsFrame.CmdMemoryAreaRead,
            areaCode: 0x82, wordAddress: 100, bitAddress: 0, count: 2, writeData: []);

        Assert.Equal(
        [
            0x80,                               // ICF
            0x00, 0x02,                         // RSV, GCT
            0x00, 0x0A, 0x00,                   // DNA, DA1, DA2
            0x00, 0x19, 0x00,                   // SNA, SA1, SA2
            0x07,                               // SID
            0x01, 0x01,                         // memory area read
            0x00, 0x82,                         // DM word area
            0x00, 0x64,                         // word 100
            0x00,                               // bit offset
            0x00, 0x02,                         // 2 words
        ], frame);

        // Wrapped for TCP: magic + big-endian length + frame
        var packet = FinsFrame.WrapFinsFrame(frame);
        Assert.Equal(0x46, packet[0]);
        Assert.Equal(0x00, packet[4]);
        Assert.Equal(frame.Length, packet[7]);
        Assert.Equal(frame, packet[8..]);
    }

    [Fact]
    public void Bit_read_uses_bit_area_code_and_bit_offset()
    {
        var frame = FinsFrame.BuildFinsCommand(
            0, 0x19, 0, 0x0A, 0x01, FinsFrame.CmdMemoryAreaRead,
            areaCode: 0x30, wordAddress: 100, bitAddress: 5, count: 1, writeData: []);

        // area 0030 = CIO bit, word 0064, bit 05
        Assert.Equal(0x00, frame[12]);
        Assert.Equal(0x30, frame[13]);
        Assert.Equal(0x00, frame[14]);
        Assert.Equal(0x64, frame[15]);
        Assert.Equal(0x05, frame[16]);
    }

    [Fact]
    public void Response_parsing_checks_icf_sid_and_response_code()
    {
        var ok = new byte[]
        {
            0xC0, 0x00, 0x02,
            0x00, 0x0A, 0x00,
            0x00, 0x19, 0x00,
            0x07,
            0x00, 0x00,                         // response code OK
            0x12, 0x34,                         // data
        };

        var data = FinsFrame.ParseFinsResponse(ok, expectedSid: 0x07);
        Assert.Equal(new byte[] { 0x12, 0x34 }, data.ToArray());

        var wrongSid = (byte[])ok.Clone();
        wrongSid[9] = 0x09;
        Assert.Throws<InvalidDataException>(() => FinsFrame.ParseFinsResponse(wrongSid, 0x07));

        var rejected = (byte[])ok.Clone();
        rejected[11] = 0x04; // response code 0x0104 (address out of range)
        rejected[10] = 0x01;
        var ex = Assert.Throws<FinsResponseCodeException>(() => FinsFrame.ParseFinsResponse(rejected, 0x07));
        Assert.Equal((ushort)0x0104, ex.ResponseCode);
    }

    [Fact]
    public void Tcp_unwrap_validates_length()
    {
        var frame = new byte[] { 0xC0, 0x00, 0x02, 0, 0x0A, 0, 0, 0x19, 0, 0x07, 0, 0 };
        var packet = FinsFrame.WrapFinsFrame(frame);

        Assert.Equal(frame, FinsFrame.UnwrapFinsFrame(packet).ToArray());

        var broken = (byte[])packet.Clone();
        broken[7] = 0x63; // corrupt length
        Assert.Throws<InvalidDataException>(() => FinsFrame.UnwrapFinsFrame(broken));
    }

    [Fact]
    public void Words_are_big_endian_and_bits_one_byte_each()
    {
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, FinsFrame.WordsToBeBytes([0x1234, 0x5678]));
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, FinsFrame.WordsFromBeBytes([0x12, 0x34, 0x56, 0x78], 2));
        Assert.Equal(new byte[] { 1, 0, 1 }, FinsFrame.BitsToBytes([true, false, true]));
        Assert.Equal(new[] { true, false, true }, FinsFrame.BitsFromBytes([1, 0, 1], 3));
    }
}

public class FinsAddressTests
{
    [Theory]
    [InlineData("CIO100", "CIO", 100, 0, false)]
    [InlineData("cio0", "CIO", 0, 0, false)]
    [InlineData("W100", "W", 100, 0, false)]
    [InlineData("H50", "H", 50, 0, false)]
    [InlineData("DM100", "DM", 100, 0, false)]
    [InlineData("D100", "DM", 100, 0, false)]
    [InlineData("CIO100.5", "CIO", 100, 5, true)]
    [InlineData("DM100.15", "DM", 100, 15, true)]
    [InlineData("W10.0", "W", 10, 0, true)]
    public void Parses_word_and_bit_forms(string input, string area, int offset, int bit, bool isBit)
    {
        var parsed = FinsAddress.Parse(input);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(bit, parsed.Bit);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DM")]
    [InlineData("X100")]
    [InlineData("DMabc")]
    [InlineData("DM-5")]
    [InlineData("DM100.16")]
    [InlineData("DM100.99999")]
    [InlineData("DM100.5.6")]
    [InlineData("DM70000")] // > 0xFFFF
    public void Invalid_addresses_throw(string input)
    {
        Assert.Throws<FormatException>(() => FinsAddress.Parse(input));
    }

    [Fact]
    public void Area_codes_match_fins_table()
    {
        Assert.Equal(0xB0, FinsAddress.GetArea("CIO").WordCode);
        Assert.Equal(0x30, FinsAddress.GetArea("CIO").BitCode);
        Assert.Equal(0xB1, FinsAddress.GetArea("W").WordCode);
        Assert.Equal(0xB2, FinsAddress.GetArea("H").WordCode);
        Assert.Equal(0x82, FinsAddress.GetArea("DM").WordCode);
        Assert.Equal(0x02, FinsAddress.GetArea("DM").BitCode);
    }
}
