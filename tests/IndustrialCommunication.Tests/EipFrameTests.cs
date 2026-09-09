using System.Buffers.Binary;
using IndustrialCommunication.Rockwell;
using Xunit;

namespace IndustrialCommunication.Tests;

public class EipFrameTests
{
    [Fact]
    public void Register_session_request_matches_reference_bytes()
    {
        Assert.Equal(
        [
            0x65, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
        ], EipFrame.BuildRegisterSession());
    }

    [Fact]
    public void Read_tag_frame_matches_reference_bytes()
    {
        // Read "MyDint" (1 element) on slot 0 — cross-checked byte-for-byte against
        // libplctag/pylogix reference frames.
        var ioi = EipTagPath.Parse("MyDint").Ioi;
        var frame = EipFrame.BuildReadTagRequest(0x1234_5678, senderContext: 1, slot: 0, ioi, elementCount: 1);

        Assert.Equal(
        [
            0x6F, 0x00, 0x2A, 0x00,                   // SendRRData, length 42
            0x78, 0x56, 0x34, 0x12,                   // session handle LE
            0x00, 0x00, 0x00, 0x00,                   // status
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // sender context
            0x00, 0x00, 0x00, 0x00,                   // options
            0x00, 0x00, 0x00, 0x00,                   // interface handle
            0x01, 0x00,                               // timeout
            0x02, 0x00,                               // item count
            0x00, 0x00, 0x00, 0x00,                   // null address item
            0xB2, 0x00, 0x1A, 0x00,                   // unconnected data item, len 26
            0x52, 0x02, 0x20, 0x06, 0x24, 0x01,       // Unconnected Send → Connection Manager
            0x0A, 0x0E, 0x0C, 0x00,                   // tick, embedded length 12
            0x4C, 0x04,                               // Read Tag, path 4 words
            0x91, 0x06, 0x4D, 0x79, 0x44, 0x69, 0x6E, 0x74, // "MyDint"
            0x01, 0x00,                               // element count
            0x01, 0x00, 0x01, 0x00,                   // route: 1 word, port 1 (backplane), slot 0
        ], frame);
    }

    [Fact]
    public void Response_parse_extracts_type_and_le_value()
    {
        byte[] response =
        [
            0x6F, 0x00, 0x1A, 0x00, 0x78, 0x56, 0x34, 0x12,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xB2, 0x00, 0x0A, 0x00,
            0xCC, 0x00, 0x00, 0x00,                   // reply 0xCC, reserved, status 0, no additional
            0xC4, 0x00,                               // DINT
            0x78, 0x56, 0x34, 0x12,                   // 0x12345678 LE
        ];

        var parsed = EipFrame.ParseCipResponse(response, EipFrame.ServiceReadTag);

        Assert.Equal(EipFrame.TypeDint, parsed.DataType!.Value);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, parsed.Data.ToArray());
    }

    [Fact]
    public void Response_status_maps_to_exception()
    {
        byte[] rejected =
        [
            0x6F, 0x00, 0x14, 0x00, 0x78, 0x56, 0x34, 0x12,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xB2, 0x00, 0x04, 0x00,
            0xCC, 0x00, 0x04, 0x00,                   // status 0x04 = path segment error
        ];

        var ex = Assert.Throws<EipCipException>(() => EipFrame.ParseCipResponse(rejected, EipFrame.ServiceReadTag));
        Assert.Equal((byte)0x04, ex.Status);
    }

    [Fact]
    public void Read_fragmented_request_appends_byte_offset()
    {
        var ioi = EipTagPath.Parse("MyDint").Ioi;
        var frame = EipFrame.BuildReadTagFragmentedRequest(
            0x1234_5678, senderContext: 1, slot: 0, ioi, elementCount: 40, byteOffset: 0x00_00_01_00);

        // Same layout as the 0x4C read plus a UDINT byte offset after the element count
        Assert.Equal(EipFrame.ServiceReadTagFragmented, frame[50]);
        var countAt = 52 + (frame[51] * 2);
        Assert.Equal(40, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(countAt)));
        Assert.Equal(0x00, frame[countAt + 2]);
        Assert.Equal(0x01, frame[countAt + 3]);
        Assert.Equal(0x00, frame[countAt + 4]);
        Assert.Equal(0x00, frame[countAt + 5]);
        Assert.Equal(countAt + 6, frame.Length - 4); // route tail follows immediately
    }

    [Fact]
    public void Write_fragmented_request_carries_offset_before_data()
    {
        var ioi = EipTagPath.Parse("MyDint").Ioi;
        var frame = EipFrame.BuildWriteTagFragmentedRequest(
            0x1234_5678, senderContext: 1, slot: 0, ioi, typeCode: EipFrame.TypeInt, elementCount: 5,
            byteOffset: 8, [0x34, 0x12, 0x78, 0x56]);

        Assert.Equal(EipFrame.ServiceWriteTagFragmented, frame[50]);
        var afterIoi = 52 + (frame[51] * 2);
        Assert.Equal(EipFrame.TypeInt, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(afterIoi)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(afterIoi + 2)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(afterIoi + 4)));
        Assert.Equal(new byte[] { 0x34, 0x12, 0x78, 0x56 }, frame[(afterIoi + 8)..(afterIoi + 12)]);
    }

    [Fact]
    public void Partial_status_yields_data_when_allowed()
    {
        byte[] partial =
        [
            0x6F, 0x00, 0x1A, 0x00, 0x78, 0x56, 0x34, 0x12,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xB2, 0x00, 0x06, 0x00,
            0xD2, 0x00, 0x06, 0x00,                   // reply 0xD2, status 0x06 (partial)
            0xC4, 0x00,                               // DINT
            0x78, 0x56, 0x34, 0x12,
        ];

        var parsed = EipFrame.ParseCipResponse(partial, EipFrame.ServiceReadTagFragmented, allowPartial: true);
        Assert.Equal(EipFrame.StatusPartialData, parsed.GeneralStatus);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, parsed.Data.ToArray());

        // without the flag, 0x06 is an error again
        Assert.Throws<EipCipException>(() => EipFrame.ParseCipResponse(partial, EipFrame.ServiceReadTagFragmented));
    }

    [Fact]
    public void Structure_reply_carries_a_four_byte_type_field()
    {
        // UDT reply: 0xCC, status 0, type 0xA0 02 + structure handle 0x0FCE, then raw member bytes.
        byte[] structureReply =
        [
            0x6F, 0x00, 0x1E, 0x00, 0x78, 0x56, 0x34, 0x12,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xB2, 0x00, 0x0E, 0x00,
            0xCC, 0x00, 0x00, 0x00,
            0xA0, 0x02, 0xCE, 0x0F,                   // structure type + handle
            0x78, 0x56, 0x34, 0x12, 0xAB, 0xCD,       // raw member bytes
        ];

        var parsed = EipFrame.ParseCipResponse(structureReply, EipFrame.ServiceReadTag);

        Assert.Equal(EipFrame.TypeStructure, parsed.DataType);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12, 0xAB, 0xCD }, parsed.Data.ToArray());
    }

    [Fact]
    public void Words_are_little_endian()
    {
        Assert.Equal(new byte[] { 0x34, 0x12, 0x78, 0x56 }, EipFrame.WordsToLeBytes([0x1234, 0x5678]));
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, EipFrame.WordsFromLeBytes([0x34, 0x12, 0x78, 0x56], 2));
    }
}

public class EipTagPathTests
{
    [Fact]
    public void Symbol_segments_match_reference_ioi()
    {
        Assert.Equal(
            new byte[] { 0x91, 0x06, 0x4D, 0x79, 0x44, 0x69, 0x6E, 0x74 },
            EipTagPath.Parse("MyDint").Ioi);

        // odd-length name gets a 0x00 pad byte
        Assert.Equal(
            new byte[] { 0x91, 0x07, 0x4D, 0x79, 0x41, 0x72, 0x72, 0x61, 0x79, 0x00, 0x28, 0x07 },
            EipTagPath.Parse("MyArray[7]").Ioi);

        // multi-level path: one symbol segment per part
        Assert.Equal(
            new byte[] { 0x91, 0x04, 0x50, 0x49, 0x44, 0x31, 0x91, 0x08, 0x53, 0x65, 0x74, 0x70, 0x6F, 0x69, 0x6E, 0x74 },
            EipTagPath.Parse("PID1.Setpoint").Ioi);

        // multidimensional index
        Assert.Equal(
            new byte[] { 0x91, 0x07, 0x4D, 0x79, 0x41, 0x72, 0x72, 0x61, 0x79, 0x00, 0x28, 0x02, 0x28, 0x05 },
            EipTagPath.Parse("MyArray[2,5]").Ioi);
    }

    [Fact]
    public void Index_segments_scale_by_width_little_endian()
    {
        // "MyArray2" is an 8-character symbol → 10-byte symbol segment first
        Assert.Equal(new byte[] { 0x29, 0x00, 0x2C, 0x01 }, EipTagPath.Parse("MyArray2[300]").Ioi[10..]);
        Assert.Equal(
            new byte[] { 0x2A, 0x00, 0xA0, 0x86, 0x01, 0x00 },
            EipTagPath.Parse("MyArray3[100000]").Ioi[10..]);
    }

    [Fact]
    public void Trailing_bit_number_is_reported_not_encoded()
    {
        var path = EipTagPath.Parse("MyDint.3");

        Assert.True(path.HasBit);
        Assert.Equal(3, path.Bit);
        Assert.Equal(new byte[] { 0x91, 0x06, 0x4D, 0x79, 0x44, 0x69, 0x6E, 0x74 }, path.Ioi);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]
    [InlineData("MyTag[")]
    [InlineData("MyTag[x]")]
    [InlineData("MyDint.32")]
    [InlineData("My..Tag")]
    [InlineData("My-Tag")]
    public void Invalid_tags_throw(string input)
    {
        Assert.Throws<FormatException>(() => EipTagPath.Parse(input));
    }
}
