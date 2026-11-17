namespace RawFrameViewer.Core.Imaging;

/// <summary>픽셀 값 통계</summary>
public static class PixelStatistics
{
    /// <summary>최솟값과 최댓값. 자동 Window/Level 계산에 쓴다.</summary>
    public static (ushort Min, ushort Max) MinMax(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.IsEmpty)
        {
            throw new ArgumentException("픽셀이 없습니다.", nameof(pixels));
        }

        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;
        foreach (ushort value in pixels)
        {
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }

        return (min, max);
    }
}
