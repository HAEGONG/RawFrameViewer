using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using RawFrameViewer.Core.Protocol;

namespace RawFrameViewer.Core.Tests.Protocol;

/// <summary>docs/protocol.md §2~§4 헤더 규칙 검증</summary>
public class FrameHeaderTests
{
    [Fact(DisplayName = "§4 CRC-32 설정이 명세의 검증값과 일치한다")]
    public void Crc32_MatchesSpecCheckValue()
    {
        uint crc = Crc32.HashToUInt32(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact(DisplayName = "§3.1 헤더 길이는 36바이트, 고정 부분은 40바이트")]
    public void Sizes_MatchSpec()
    {
        Assert.Equal(36, ProtocolConstants.HeaderSize);
        Assert.Equal(40, ProtocolConstants.FixedOverhead);
    }

    [Fact(DisplayName = "§3.1 헤더를 쓰고 다시 읽으면 같은 값이 나온다")]
    public void WriteThenParse_RoundTrips()
    {
        FrameHeader original = FrameHeader.ForFrame(frameId: 42, timestampUs: 1_234_567_890_123, width: 2048, height: 1536, bitsStored: 14);
        byte[] buffer = new byte[ProtocolConstants.HeaderSize];
        original.WriteTo(buffer);

        HeaderStatus status = FrameHeader.TryParse(buffer, out FrameHeader parsed, out _);

        Assert.Equal(HeaderStatus.Ok, status);
        Assert.Equal(original, parsed);
    }

    [Fact(DisplayName = "§2 §3.1 필드가 명세의 오프셋에 Little-endian으로 놓인다")]
    public void Write_UsesSpecOffsetsAndLittleEndian()
    {
        FrameHeader header = FrameHeader.ForFrame(frameId: 0x01020304, timestampUs: 0x0102030405060708, width: 2048, height: 0x0A0B, bitsStored: 14);
        byte[] b = new byte[ProtocolConstants.HeaderSize];
        header.WriteTo(b);

        Assert.Equal("RFVF"u8.ToArray(), b[0..4]);
        Assert.Equal(new byte[] { 0x01, 0x00 }, b[4..6]);                          // Version 1
        Assert.Equal(new byte[] { 0x01, 0x00 }, b[6..8]);                          // MessageType Frame
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, b[8..12]);             // FrameId
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, b[12..20]); // Timestamp
        Assert.Equal(new byte[] { 0x00, 0x08 }, b[20..22]);                        // Width 2048 → 00 08 (명세 §2 예시)
        Assert.Equal(new byte[] { 0x0B, 0x0A }, b[22..24]);                        // Height
        Assert.Equal(14, b[24]);                                                   // BitsStored
        Assert.Equal(new byte[] { 0, 0, 0 }, b[25..28]);                           // Reserved
        Assert.Equal(2048u * 0x0A0B * 2, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(28)));
        Assert.Equal(Crc32.HashToUInt32(b.AsSpan(0, 32)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32)));
    }

    [Fact(DisplayName = "§6.1② Magic이 다르면 BadMagic")]
    public void Parse_WrongMagic_ReturnsBadMagic()
    {
        byte[] b = ValidHeaderBytes();
        b[0] = (byte)'X';

        Assert.Equal(HeaderStatus.BadMagic, FrameHeader.TryParse(b, out _, out _));
    }

    [Theory(DisplayName = "§6.1③ CRC 대상 영역의 어느 바이트가 바뀌어도 BadHeaderCrc")]
    [InlineData(4)]   // Version
    [InlineData(8)]   // FrameId
    [InlineData(20)]  // Width
    [InlineData(28)]  // PayloadLength
    [InlineData(31)]  // CRC 대상의 마지막 바이트
    [InlineData(32)]  // HeaderCrc 자체
    public void Parse_CorruptedByte_ReturnsBadHeaderCrc(int offset)
    {
        byte[] b = ValidHeaderBytes();
        b[offset] ^= 0xFF;

        Assert.Equal(HeaderStatus.BadHeaderCrc, FrameHeader.TryParse(b, out _, out _));
    }

    [Fact(DisplayName = "§6.1④ CRC가 맞아도 버전이 다르면 UnsupportedVersion")]
    public void Parse_OtherVersion_ReturnsUnsupportedVersion()
    {
        byte[] b = ValidHeaderBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2);
        RecomputeHeaderCrc(b);

        Assert.Equal(HeaderStatus.UnsupportedVersion, FrameHeader.TryParse(b, out _, out string? error));
        Assert.NotNull(error);
    }

    public static TheoryData<string, FrameHeader> InvalidHeaders => new()
    {
        { "FrameId 0", new FrameHeader(MessageType.Frame, 0, 0, 4, 3, 14, 24) },
        { "Width 0", new FrameHeader(MessageType.Frame, 1, 0, 0, 3, 14, 0) },
        { "Width 4097", new FrameHeader(MessageType.Frame, 1, 0, 4097, 1, 14, 4097 * 2) },
        { "Height 4097", new FrameHeader(MessageType.Frame, 1, 0, 1, 4097, 14, 4097 * 2) },
        { "BitsStored 7", new FrameHeader(MessageType.Frame, 1, 0, 4, 3, 7, 24) },
        { "BitsStored 17", new FrameHeader(MessageType.Frame, 1, 0, 4, 3, 17, 24) },
        { "PayloadLength 불일치", new FrameHeader(MessageType.Frame, 1, 0, 4, 3, 14, 23) },
        { "Heartbeat에 페이로드", new FrameHeader(MessageType.Heartbeat, 1, 0, 0, 0, 0, 2) },
        { "Heartbeat에 영상 크기", new FrameHeader(MessageType.Heartbeat, 1, 0, 4, 3, 0, 0) },
        { "알 수 없는 MessageType", new FrameHeader((MessageType)9, 1, 0, 0, 0, 0, 0) },
    };

    [Theory(DisplayName = "§6.1⑤ CRC가 맞아도 필드 값이 규칙을 어기면 InvalidField")]
    [MemberData(nameof(InvalidHeaders))]
    public void Parse_InvalidField_ReturnsInvalidField(string caseName, FrameHeader header)
    {
        byte[] b = new byte[ProtocolConstants.HeaderSize];
        header.WriteTo(b);

        HeaderStatus status = FrameHeader.TryParse(b, out _, out string? error);

        Assert.True(status == HeaderStatus.InvalidField, $"{caseName}: {status}");
        Assert.NotNull(error);
    }

    [Fact(DisplayName = "§3.1 Reserved가 0이 아니면 InvalidField")]
    public void Parse_NonZeroReserved_ReturnsInvalidField()
    {
        byte[] b = ValidHeaderBytes();
        b[25] = 1;
        RecomputeHeaderCrc(b);

        Assert.Equal(HeaderStatus.InvalidField, FrameHeader.TryParse(b, out _, out _));
    }

    [Fact(DisplayName = "§3.1 최대 크기 4096×4096은 허용된다")]
    public void Parse_MaxDimension_IsOk()
    {
        byte[] b = new byte[ProtocolConstants.HeaderSize];
        FrameHeader.ForFrame(1, 0, 4096, 4096, 16).WriteTo(b);

        Assert.Equal(HeaderStatus.Ok, FrameHeader.TryParse(b, out FrameHeader parsed, out _));
        Assert.Equal(33_554_432u, parsed.PayloadLength);
    }

    private static byte[] ValidHeaderBytes()
    {
        byte[] b = new byte[ProtocolConstants.HeaderSize];
        FrameHeader.ForFrame(7, 123, 4, 3, 14).WriteTo(b);
        return b;
    }

    private static void RecomputeHeaderCrc(byte[] b) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), Crc32.HashToUInt32(b.AsSpan(0, 32)));
}
