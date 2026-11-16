using System.Buffers.Binary;
using System.IO.Hashing;

namespace RawFrameViewer.Core.Protocol;

/// <summary>헤더 파싱 결과. §6.1 처리 순서 ②~⑤에 대응한다.</summary>
public enum HeaderStatus
{
    Ok,
    /// <summary>시작 표식 불일치 → 재동기화</summary>
    BadMagic,
    /// <summary>헤더 CRC 불일치 → 재동기화</summary>
    BadHeaderCrc,
    /// <summary>지원하지 않는 버전 → 연결 종료</summary>
    UnsupportedVersion,
    /// <summary>CRC는 맞지만 필드 값이 규칙 위반 → 연결 종료</summary>
    InvalidField,
}

/// <summary>
/// 메시지 헤더(36바이트). 필드 배치는 docs/protocol.md §3.1을 따른다.
/// 모든 다중 바이트 정수는 Little-endian으로 읽고 쓴다.
/// </summary>
public readonly record struct FrameHeader(
    MessageType MessageType,
    uint FrameId,
    long TimestampUs,
    ushort Width,
    ushort Height,
    byte BitsStored,
    uint PayloadLength)
{
    // 필드 오프셋 (§3.1 표와 1:1)
    private const int OffsetVersion = 4;
    private const int OffsetMessageType = 6;
    private const int OffsetFrameId = 8;
    private const int OffsetTimestamp = 12;
    private const int OffsetWidth = 20;
    private const int OffsetHeight = 22;
    private const int OffsetBitsStored = 24;
    private const int OffsetReserved1 = 25;
    private const int OffsetReserved2 = 26;
    private const int OffsetPayloadLength = 28;
    private const int OffsetHeaderCrc = 32;

    /// <summary>영상 프레임 헤더를 만든다. PayloadLength는 가로×세로×2로 계산한다.</summary>
    public static FrameHeader ForFrame(uint frameId, long timestampUs, ushort width, ushort height, byte bitsStored)
    {
        // ushort × ushort를 int로 곱하면 큰 값에서 넘칠 수 있으므로 long으로 계산한다.
        long payloadLength = (long)width * height * 2;
        if (payloadLength > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "영상 크기가 너무 큽니다.");
        }

        return new FrameHeader(MessageType.Frame, frameId, timestampUs, width, height, bitsStored, (uint)payloadLength);
    }

    /// <summary>Heartbeat 헤더를 만든다. 영상 관련 필드는 모두 0이다.</summary>
    public static FrameHeader ForHeartbeat(uint lastFrameId, long timestampUs) =>
        new(MessageType.Heartbeat, lastFrameId, timestampUs, 0, 0, 0, 0);

    /// <summary>필드 값 규칙(§3.1)을 검사한다. 위반이면 이유를, 정상이면 null을 반환한다.</summary>
    public string? Validate()
    {
        switch (MessageType)
        {
            case MessageType.Frame:
                if (FrameId == 0) return "Frame의 FrameId는 1 이상이어야 합니다.";
                if (Width is 0 or > ProtocolConstants.MaxDimension) return $"Width {Width}가 허용 범위(1~{ProtocolConstants.MaxDimension})를 벗어났습니다.";
                if (Height is 0 or > ProtocolConstants.MaxDimension) return $"Height {Height}가 허용 범위(1~{ProtocolConstants.MaxDimension})를 벗어났습니다.";
                if (BitsStored is < ProtocolConstants.MinBitsStored or > ProtocolConstants.MaxBitsStored) return $"BitsStored {BitsStored}가 허용 범위({ProtocolConstants.MinBitsStored}~{ProtocolConstants.MaxBitsStored})를 벗어났습니다.";
                long expected = (long)Width * Height * 2;
                if (PayloadLength != expected) return $"PayloadLength {PayloadLength}가 Width×Height×2({expected})와 다릅니다.";
                return null;

            case MessageType.Heartbeat:
                if (Width != 0 || Height != 0 || BitsStored != 0 || PayloadLength != 0) return "Heartbeat의 영상 필드와 PayloadLength는 모두 0이어야 합니다.";
                return null;

            default:
                return $"알 수 없는 MessageType {(ushort)MessageType}입니다.";
        }
    }

    /// <summary>헤더 36바이트를 쓴다. 마지막 4바이트에 HeaderCrc를 계산해 넣는다.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ProtocolConstants.HeaderSize)
        {
            throw new ArgumentException($"버퍼가 {ProtocolConstants.HeaderSize}바이트보다 작습니다.", nameof(destination));
        }

        Span<byte> h = destination[..ProtocolConstants.HeaderSize];
        ProtocolConstants.Magic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[OffsetVersion..], ProtocolConstants.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[OffsetMessageType..], (ushort)MessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(h[OffsetFrameId..], FrameId);
        BinaryPrimitives.WriteInt64LittleEndian(h[OffsetTimestamp..], TimestampUs);
        BinaryPrimitives.WriteUInt16LittleEndian(h[OffsetWidth..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(h[OffsetHeight..], Height);
        h[OffsetBitsStored] = BitsStored;
        h[OffsetReserved1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(h[OffsetReserved2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[OffsetPayloadLength..], PayloadLength);

        uint crc = Crc32.HashToUInt32(h[..ProtocolConstants.HeaderCrcCoveredSize]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[OffsetHeaderCrc..], crc);
    }

    /// <summary>
    /// 헤더 36바이트를 해석한다. 검사 순서는 §6.1 ②Magic → ③HeaderCrc → ④Version → ⑤필드 값.
    /// </summary>
    /// <param name="error">InvalidField나 UnsupportedVersion일 때 사람이 읽을 수 있는 이유</param>
    public static HeaderStatus TryParse(ReadOnlySpan<byte> source, out FrameHeader header, out string? error)
    {
        if (source.Length < ProtocolConstants.HeaderSize)
        {
            throw new ArgumentException($"버퍼가 {ProtocolConstants.HeaderSize}바이트보다 작습니다.", nameof(source));
        }

        header = default;
        error = null;
        ReadOnlySpan<byte> h = source[..ProtocolConstants.HeaderSize];

        if (!h[..ProtocolConstants.Magic.Length].SequenceEqual(ProtocolConstants.Magic))
        {
            return HeaderStatus.BadMagic;
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[OffsetHeaderCrc..]);
        uint actualCrc = Crc32.HashToUInt32(h[..ProtocolConstants.HeaderCrcCoveredSize]);
        if (expectedCrc != actualCrc)
        {
            return HeaderStatus.BadHeaderCrc;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h[OffsetVersion..]);
        if (version != ProtocolConstants.Version)
        {
            error = $"지원하지 않는 프로토콜 버전 {version}입니다. (지원: {ProtocolConstants.Version})";
            return HeaderStatus.UnsupportedVersion;
        }

        if (h[OffsetReserved1] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(h[OffsetReserved2..]) != 0)
        {
            error = "Reserved 필드는 0이어야 합니다.";
            return HeaderStatus.InvalidField;
        }

        header = new FrameHeader(
            (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(h[OffsetMessageType..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[OffsetFrameId..]),
            BinaryPrimitives.ReadInt64LittleEndian(h[OffsetTimestamp..]),
            BinaryPrimitives.ReadUInt16LittleEndian(h[OffsetWidth..]),
            BinaryPrimitives.ReadUInt16LittleEndian(h[OffsetHeight..]),
            h[OffsetBitsStored],
            BinaryPrimitives.ReadUInt32LittleEndian(h[OffsetPayloadLength..]));

        error = header.Validate();
        return error is null ? HeaderStatus.Ok : HeaderStatus.InvalidField;
    }
}
