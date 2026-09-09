using IndustrialCommunication.Mitsubishi;
using Xunit;

namespace IndustrialCommunication.Tests;

public class McFrameTests
{
    // Golden bytes cross-checked against the MELSEC MC-protocol 3E/4E binary format.
    [Fact]
    public void Bit_write_request_matches_reference_bytes()
    {
        // Write [true, false, true, true] to M0, monitor timer 0 ms, 3E frame.
        var request = McFrame.BuildRequest(
            McFrameKind.Frame3E, serial: 0, networkNo: 0x00, pcNo: 0xFF, stationNo: 0x00,
            monitorTimerMs: 0, command: McFrame.CmdBatchWrite, subCommand: McFrame.SubCommandBit,
            deviceNumber: 0, deviceCode: 0x90, count: 4, writeData: [0x10, 0x11]);

        Assert.Equal(
        [
            0x50, 0x00,                     // subheader 3E
            0x00, 0xFF, 0xFF, 0x03, 0x00,   // route
            0x0E, 0x00,                     // request data length = 14
            0x00, 0x00,                     // monitor timer (250 ms units)
            0x01, 0x14,                     // command 0x1401 (batch write)
            0x01, 0x00,                     // subcommand 0x0001 (bit)
            0x00, 0x00, 0x00,               // device address
            0x90,                           // device code M
            0x04, 0x00,                     // device count
            0x10, 0x11,                     // bit data, 2 bits per byte
        ], request);
    }

    [Fact]
    public void Bit_read_request_matches_reference_bytes()
    {
        var request = McFrame.BuildRequest(
            McFrameKind.Frame3E, serial: 0, networkNo: 0x00, pcNo: 0xFF, stationNo: 0x00,
            monitorTimerMs: 0, command: McFrame.CmdBatchRead, subCommand: McFrame.SubCommandBit,
            deviceNumber: 0, deviceCode: 0x90, count: 6, writeData: []);

        Assert.Equal(
        [
            0x50, 0x00,
            0x00, 0xFF, 0xFF, 0x03, 0x00,
            0x0C, 0x00,                     // length = 12
            0x00, 0x00,
            0x01, 0x04,                     // command 0x0401 (batch read)
            0x01, 0x00,
            0x00, 0x00, 0x00,
            0x90,
            0x06, 0x00,
        ], request);
    }

    [Fact]
    public void Frame4E_adds_serial_number()
    {
        var request = McFrame.BuildRequest(
            McFrameKind.Frame4E, serial: 0x1234, networkNo: 0x00, pcNo: 0xFF, stationNo: 0x00,
            monitorTimerMs: 0, command: McFrame.CmdBatchRead, subCommand: McFrame.SubCommandWord,
            deviceNumber: 0, deviceCode: 0xA8, count: 1, writeData: []);

        Assert.Equal(0x54, request[0]);
        Assert.Equal(0x00, request[1]);
        Assert.Equal(0x34, request[2]); // serial low byte
        Assert.Equal(0x12, request[3]); // serial high byte
        Assert.Equal(0xA8, request[20]); // device code: subheader2+serial2+route5+len2+mon2+cmd2+sub2+addr3
    }

    [Fact]
    public void Monitor_timer_is_stored_in_250ms_units()
    {
        var request = McFrame.BuildRequest(
            McFrameKind.Frame3E, serial: 0, networkNo: 0, pcNo: 0xFF, stationNo: 0,
            monitorTimerMs: 3000, command: McFrame.CmdBatchRead, subCommand: McFrame.SubCommandWord,
            deviceNumber: 0, deviceCode: 0xA8, count: 1, writeData: []);

        // header (2+5+2) then monitor timer
        Assert.Equal(12, request[9]); // 3000 / 250
        Assert.Equal(0, request[10]);
    }

    [Fact]
    public void Response_parse_returns_payload_and_unpacked_bits()
    {
        var response = new byte[]
        {
            0xD0, 0x00,
            0x00, 0xFF, 0xFF, 0x03, 0x00,
            0x05, 0x00,                     // length = end code (2) + 3 data bytes
            0x00, 0x00,                     // end code OK
            0x01, 0x11, 0x10,               // 6 bits
        };

        var data = McFrame.ParseResponse(response, McFrameKind.Frame3E, expectedSerial: 0);

        Assert.Equal(ResponseBits, data.ToArray());
        Assert.Equal(Bits2To5, McFrame.UnpackBits(data.Span, 6));
    }

    private static readonly byte[] BadSubheader = [0xD0, 0x01, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00];
    private static readonly byte[] WrongSerial = [0xD4, 0x00, 0x99, 0x99, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00];
    private static readonly byte[] Rejected = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x01, 0xC0];
    private static readonly byte[] LengthMismatch = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x05, 0x00, 0x00, 0x00, 0x12, 0x34];
    private static readonly byte[] ResponseBits = [0x01, 0x11, 0x10];
    private static readonly bool[] Bits2To5 = [false, true, true, true, true, false];

    [Fact]
    public void Response_parse_checks_serial_length_and_end_code()
    {
        Assert.Throws<InvalidDataException>(() =>
            McFrame.ParseResponse(BadSubheader, McFrameKind.Frame3E, expectedSerial: 0));

        Assert.Throws<InvalidDataException>(() =>
            McFrame.ParseResponse(WrongSerial, McFrameKind.Frame4E, expectedSerial: 1));

        var ex = Assert.Throws<McEndCodeException>(() =>
            McFrame.ParseResponse(Rejected, McFrameKind.Frame3E, expectedSerial: 0));
        Assert.Equal((ushort)0xC001, ex.EndCode);

        Assert.Throws<InvalidDataException>(() =>
            McFrame.ParseResponse(LengthMismatch, McFrameKind.Frame3E, expectedSerial: 0));
    }

    [Theory]
    [InlineData(new[] { true, false, true, true }, new byte[] { 0x10, 0x11 })]
    [InlineData(new[] { true, true }, new byte[] { 0x11 })]
    [InlineData(new[] { false }, new byte[] { 0x00 })]
    public void PackBits_two_bits_per_byte(bool[] bits, byte[] expected)
    {
        Assert.Equal(expected, McFrame.PackBits(bits));
        Assert.Equal(bits, McFrame.UnpackBits(expected, bits.Length));
    }

    [Fact]
    public void UnpackBits_odd_count_ignores_padding_nibble()
    {
        // byte0 = bit0(high)=1, bit1(low)=0; byte1 = bit2(high)=1, padding(low)=0
        Assert.Equal(new[] { true, false, true }, McFrame.UnpackBits([0x10, 0x10], 3));
    }

    [Fact]
    public void Words_use_little_endian_bytes()
    {
        Assert.Equal(new byte[] { 0x34, 0x12, 0x78, 0x56 }, McFrame.WordsToLeBytes([0x1234, 0x5678]));
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, McFrame.WordsFromLeBytes([0x34, 0x12, 0x78, 0x56], 2));
    }
}

public class McAddressTests
{
    [Theory]
    [InlineData("D100", "D", 100, false)]
    [InlineData("W1F", "W", 31, false)]
    [InlineData("ZR70000", "ZR", 70000, false)]
    [InlineData("M100", "M", 100, true)]
    [InlineData("X0A", "X", 10, true)]
    [InlineData("Y20", "Y", 32, true)]
    [InlineData("B0", "B", 0, true)]
    [InlineData("SB10", "SB", 16, true)]
    [InlineData("SW20", "SW", 32, false)]
    public void Word_vs_bit_and_hex_vs_decimal(string address, string area, int offset, bool isBit)
    {
        var parsed = McAddress.Parse(address);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(isBit, parsed.IsBit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]
    [InlineData("Q100")]
    [InlineData("Dabc")]
    [InlineData("D-5")]
    [InlineData("M16777216")] // > 0xFFFFFF
    public void Invalid_addresses_throw(string input)
    {
        Assert.Throws<FormatException>(() => McAddress.Parse(input));
    }

    [Fact]
    public void Device_codes_match_slmp_table()
    {
        Assert.Equal(0xA8, McAddress.GetDevice("D").Code);
        Assert.Equal(0x90, McAddress.GetDevice("M").Code);
        Assert.Equal(0x9C, McAddress.GetDevice("X").Code);
        Assert.Equal(0xB4, McAddress.GetDevice("W").Code);
        Assert.True(McAddress.GetDevice("D").IsWord);
        Assert.False(McAddress.GetDevice("M").IsWord);
    }
}
