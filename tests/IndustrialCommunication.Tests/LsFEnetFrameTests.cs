using IndustrialCommunication.LsFEnet;
using Xunit;

namespace IndustrialCommunication.Tests;

public class LsFEnetFrameTests
{
    private const byte Xgk = 0xA0;

    // Golden bytes from the LS manuals (XBL-EMTA V1.8 / XGL-EFMTB V3.2).
    // Note: the manual's individual-read example prints BCC 0x4E — a documented typo; the correct
    // arithmetic sum of header bytes 0..18 is 0x3C, which is what the builder produces.
    [Fact]
    public void Individual_read_word_frame_matches_reference()
    {
        var frame = LsFEnetFrame.BuildIndividualRead(invokeId: 0, cpuInfo: Xgk, ["%MW0"]);

        Assert.Equal(
        [
            // header
            0x4C, 0x53, 0x49, 0x53, 0x2D, 0x58, 0x47, 0x54, 0x00, 0x00, // "LSIS-XGT\0\0"
            0x00, 0x00,       // PLC info
            0xA0,             // CPU info (XGK)
            0x33,             // source: PC → PLC
            0x00, 0x00,       // invoke ID
            0x0E, 0x00,       // instruction length = 14
            0x00,             // FEnet position
            0x3C,             // BCC (corrected manual value)
            // instruction
            0x54, 0x00,       // read request
            0x02, 0x00,       // individual WORD
            0x00, 0x00,       // reserved
            0x01, 0x00,       // variable count
            0x04, 0x00,       // variable name length
            0x25, 0x4D, 0x57, 0x30, // "%MW0"
        ], frame);
    }

    [Fact]
    public void Continuous_read_frame_matches_reference()
    {
        var frame = LsFEnetFrame.BuildContinuousRead(invokeId: 1, cpuInfo: Xgk, "%MB0", byteCount: 2);

        Assert.Equal(36, frame.Length);
        Assert.Equal(0x10, frame[16]); // instruction length = 16
        Assert.Equal(0x3F, frame[19]); // BCC
        Assert.Equal(0x54, frame[20]);
        Assert.Equal(0x14, frame[22]); // continuous type
        Assert.Equal(0x02, frame[34]); // 2 bytes
    }

    [Fact]
    public void Write_word_frame_layout()
    {
        var frame = LsFEnetFrame.BuildIndividualWrite(invokeId: 0, cpuInfo: Xgk,
            [("%MW0", [0x34, 0x12])]);

        Assert.Equal(38, frame.Length);
        Assert.Equal(0x12, frame[16]); // instruction length = 18
        Assert.Equal(0x58, frame[20]); // write request
        Assert.Equal(0x02, frame[22]); // individual WORD
        Assert.Equal(0x34, frame[36]); // 0x1234 LE
        Assert.Equal(0x12, frame[37]);
    }

    [Fact]
    public void Status_request_matches_reference()
    {
        var frame = LsFEnetFrame.BuildStatusRequest(invokeId: 0, cpuInfo: Xgk);

        Assert.Equal(
        [
            0x4C, 0x53, 0x49, 0x53, 0x2D, 0x58, 0x47, 0x54, 0x00, 0x00,
            0x00, 0x00, 0xA0, 0x33, 0x00, 0x00, 0x06, 0x00, 0x00, 0x34,
            0xB0, 0x00, 0x00, 0x00, 0x00, 0x00,
        ], frame);
    }

    [Fact]
    public void Read_response_parse_extracts_data_from_offset_32()
    {
        byte[] response =
        [
            0x4C, 0x53, 0x49, 0x53, 0x2D, 0x58, 0x47, 0x54, 0x00, 0x00,
            0x11, 0x01,       // PLC info: RUN + CPUHN
            0xA0, 0x11,       // CPU info, source: PLC → PC
            0x00, 0x00,       // invoke ID echo
            0x0E, 0x00,       // instruction length
            0x03,             // FEnet position
            0x2F,             // BCC
            0x55, 0x00,       // read response
            0x02, 0x00,       // individual WORD
            0x08, 0x01,       // reserved
            0x00, 0x00,       // error status
            0x01, 0x00,       // variable count
            0x02, 0x00,       // data byte count
            0x00, 0x00,       // data (%MW0 = 0)
        ];

        var parsed = LsFEnetFrame.ParseResponse(response, expectedInvokeId: 0);

        Assert.Equal(LsFEnetFrame.CmdReadResponse, parsed.Command);
        Assert.Equal(new byte[] { 0x00, 0x00 }, parsed.Data.ToArray());
    }

    [Fact]
    public void Error_status_maps_to_exception_with_code()
    {
        byte[] rejected =
        [
            0x4C, 0x53, 0x49, 0x53, 0x2D, 0x58, 0x47, 0x54, 0x00, 0x00,
            0x00, 0x00, 0xA0, 0x11, 0x07, 0x00, 0x0C, 0x00, 0x00, 0x00,
            0x55, 0x00, 0x02, 0x00, 0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00,
        ];

        var ex = Assert.Throws<LsFEnetStatusException>(() => LsFEnetFrame.ParseResponse(rejected, expectedInvokeId: 7));
        Assert.Equal((ushort)0x0004, ex.Code);
        Assert.Contains("outside the supported device range", ex.Message);
    }

    [Fact]
    public void Invoke_id_mismatch_is_a_protocol_error()
    {
        byte[] response =
        [
            0x4C, 0x53, 0x49, 0x53, 0x2D, 0x58, 0x47, 0x54, 0x00, 0x00,
            0x00, 0x00, 0xA0, 0x11, 0x01, 0x00, 0x0C, 0x00, 0x00, 0x00,
            0x55, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
        ];

        Assert.Throws<InvalidDataException>(() => LsFEnetFrame.ParseResponse(response, expectedInvokeId: 9));
    }

    [Fact]
    public void Words_are_little_endian()
    {
        Assert.Equal(new byte[] { 0x34, 0x12 }, LsFEnetFrame.WordsToLeBytes([0x1234]));
        Assert.Equal(new ushort[] { 0x1234 }, LsFEnetFrame.WordsFromLeBytes([0x34, 0x12], 1));
    }
}

public class LsFEnetAddressTests
{
    [Theory]
    [InlineData("D100", "%DW100", false)]
    [InlineData("%MW0", "%MW0", false)]
    [InlineData("m10", "%MW10", false)]
    [InlineData("ZR500", "%ZRW500", false)]
    [InlineData("N100", "%NW100", false)]
    [InlineData("M100.3", "%MX1603", true)]
    [InlineData("M0.15", "%MX15", true)]
    [InlineData("MX1603", "%MX1603", true)]
    public void Parses_word_and_bit_forms(string input, string wire, bool isBit)
    {
        var (variable, bit) = LsFEnetAddress.ParseVariable(input);

        Assert.Equal(wire, variable);
        Assert.Equal(isBit, bit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("X100")]     // X alone is not a device letter (bit form uses word.bit)
    [InlineData("D")]
    [InlineData("Dabc")]
    [InlineData("D-5")]
    [InlineData("M100.16")]
    public void Invalid_addresses_throw(string input)
    {
        Assert.Throws<FormatException>(() => LsFEnetAddress.ParseVariable(input));
    }
}
