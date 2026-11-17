namespace RawFrameViewer.Core.Imaging;

/// <summary>사각형 관심 영역(ROI). 픽셀 좌표, 왼쪽 위 기준.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>두 점을 꼭짓점으로 하는 사각형. 드래그 방향과 관계없이 만든다.</summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);

    /// <summary>영상 경계 안으로 자른다. 겹치는 부분이 없으면 null.</summary>
    public PixelRect? ClipTo(int imageWidth, int imageHeight)
    {
        int left = Math.Max(X, 0);
        int top = Math.Max(Y, 0);
        int right = Math.Min(X + Width, imageWidth);
        int bottom = Math.Min(Y + Height, imageHeight);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : null;
    }
}

/// <summary>ROI 통계 결과</summary>
public sealed record RoiStatistics(PixelRect Area, long Count, double Mean, double StdDev, ushort Min, ushort Max)
{
    /// <summary>프레임의 ROI 안 픽셀 통계를 계산한다. ROI가 영상 밖이면 null.</summary>
    public static RoiStatistics? Compute(RawFrame frame, PixelRect roi)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (roi.ClipTo(frame.Width, frame.Height) is not { } area)
        {
            return null;
        }

        ReadOnlySpan<ushort> pixels = frame.Pixels;
        long count = (long)area.Width * area.Height;
        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;
        double sum = 0;
        double sumSquares = 0;

        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            foreach (ushort value in pixels.Slice((y * frame.Width) + area.X, area.Width))
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                sumSquares += (double)value * value;
            }
        }

        double mean = sum / count;

        // 모집단 표준편차. 부동소수점 오차로 아주 작은 음수가 나올 수 있어 0 아래는 자른다.
        double variance = Math.Max(0, (sumSquares / count) - (mean * mean));
        return new RoiStatistics(area, count, mean, Math.Sqrt(variance), min, max);
    }
}
