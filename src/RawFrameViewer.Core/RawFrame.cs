namespace RawFrameViewer.Core;

/// <summary>16-bit 원본 영상 한 장. 픽셀은 왼쪽 위부터 행 우선(row-major)으로 저장한다.</summary>
public sealed class RawFrame
{
    private readonly ushort[] _pixels;

    public RawFrame(uint frameId, long timestampUs, ushort width, ushort height, byte bitsStored, ushort[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "가로·세로는 1 이상이어야 합니다.");
        }

        if (bitsStored is 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsStored), "BitsStored는 1~16이어야 합니다.");
        }

        // ushort × ushort는 int로 계산되어 큰 값에서 넘칠 수 있으므로 long으로 곱한다.
        if (pixels.Length != (long)width * height)
        {
            throw new ArgumentException($"픽셀 수 {pixels.Length}가 {width}×{height}와 다릅니다.", nameof(pixels));
        }

        FrameId = frameId;
        TimestampUs = timestampUs;
        Width = width;
        Height = height;
        BitsStored = bitsStored;
        _pixels = pixels;
    }

    public uint FrameId { get; }
    public long TimestampUs { get; }
    public ushort Width { get; }
    public ushort Height { get; }
    public byte BitsStored { get; }

    /// <summary>BitsStored로 표현할 수 있는 최댓값. 예: 14 → 16383</summary>
    public ushort MaxValue => (ushort)((1 << BitsStored) - 1);

    public ReadOnlySpan<ushort> Pixels => _pixels;
}
