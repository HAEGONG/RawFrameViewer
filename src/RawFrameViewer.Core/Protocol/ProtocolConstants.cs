namespace RawFrameViewer.Core.Protocol;

/// <summary>프로토콜 고정값. 근거: docs/protocol.md</summary>
public static class ProtocolConstants
{
    /// <summary>헤더 길이(바이트). §3.1</summary>
    public const int HeaderSize = 36;

    /// <summary>헤더 중 CRC 계산 대상 길이(오프셋 0~31). §3.1</summary>
    public const int HeaderCrcCoveredSize = 32;

    /// <summary>페이로드 뒤에 붙는 CRC 길이. §3.3</summary>
    public const int PayloadCrcSize = 4;

    /// <summary>헤더 + 페이로드 CRC. 페이로드를 제외한 고정 길이.</summary>
    public const int FixedOverhead = HeaderSize + PayloadCrcSize;

    public const ushort Version = 1;

    /// <summary>가로·세로 최대 픽셀 수. §3.1</summary>
    public const int MaxDimension = 4096;

    public const byte MinBitsStored = 8;
    public const byte MaxBitsStored = 16;

    /// <summary>
    /// 메시지 시작 표식 "RFVF". 정수가 아니라 바이트 순서 그대로 비교한다.
    /// 정수로 읽으면 바이트 순서 해석에 따라 값이 달라질 수 있기 때문이다.
    /// </summary>
    public static ReadOnlySpan<byte> Magic => "RFVF"u8;
}
