using RawFrameViewer.Core.Imaging;

namespace RawFrameViewer.Core.Tests.Imaging;

public class MeasurementTests
{
    /// <summary>
    /// 4×3 프레임, 값 = 행 번호 × 10 + 열 번호
    ///  0  1  2  3
    /// 10 11 12 13
    /// 20 21 22 23
    /// </summary>
    private static RawFrame KnownFrame()
    {
        var pixels = new ushort[12];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                pixels[(y * 4) + x] = (ushort)((y * 10) + x);
            }
        }

        return new RawFrame(1, 0, 4, 3, 14, pixels);
    }

    [Fact(DisplayName = "ROI 통계: 손으로 계산한 값과 같다")]
    public void Roi_KnownValues()
    {
        // 영역: (1,1)~(2,2) → 11, 12, 21, 22
        RoiStatistics? stats = RoiStatistics.Compute(KnownFrame(), new PixelRect(1, 1, 2, 2));

        Assert.NotNull(stats);
        Assert.Equal(4, stats.Count);
        Assert.Equal(16.5, stats.Mean, precision: 10);
        Assert.Equal(5.0249378, stats.StdDev, precision: 6); // sqrt(((5.5²×2)+(4.5²×2))/4)
        Assert.Equal(11, stats.Min);
        Assert.Equal(22, stats.Max);
    }

    [Fact(DisplayName = "ROI 통계: 같은 값만 있으면 표준편차 0")]
    public void Roi_ConstantValues_ZeroStdDev()
    {
        var frame = new RawFrame(1, 0, 3, 3, 14, Enumerable.Repeat((ushort)5000, 9).ToArray());

        RoiStatistics? stats = RoiStatistics.Compute(frame, new PixelRect(0, 0, 3, 3));

        Assert.Equal(0, stats!.StdDev);
        Assert.Equal(5000, stats.Mean);
    }

    [Fact(DisplayName = "ROI가 영상 밖으로 나가면 겹치는 부분만 계산")]
    public void Roi_PartiallyOutside_IsClipped()
    {
        RoiStatistics? stats = RoiStatistics.Compute(KnownFrame(), new PixelRect(-5, 2, 7, 10));

        Assert.Equal(new PixelRect(0, 2, 2, 1), stats!.Area); // 20, 21
        Assert.Equal(20.5, stats.Mean);
    }

    [Fact(DisplayName = "ROI가 영상과 겹치지 않으면 null")]
    public void Roi_FullyOutside_ReturnsNull()
    {
        Assert.Null(RoiStatistics.Compute(KnownFrame(), new PixelRect(10, 10, 2, 2)));
    }

    [Fact(DisplayName = "FromCorners는 드래그 방향과 관계없이 같은 사각형을 만든다")]
    public void FromCorners_AnyDirection()
    {
        Assert.Equal(new PixelRect(1, 2, 3, 4), PixelRect.FromCorners(1, 2, 3, 5));
        Assert.Equal(new PixelRect(1, 2, 3, 4), PixelRect.FromCorners(3, 5, 1, 2));
    }

    [Fact(DisplayName = "히스토그램: 모든 픽셀이 빠짐없이 한 구간에 들어간다")]
    public void Histogram_CountsAllPixels()
    {
        ushort[] pixels = [0, 1, 100, 16383, 8192, 65535];

        Histogram histogram = Histogram.Compute(pixels, maxValue: 16383, binCount: 256);

        int total = 0;
        foreach (int count in histogram.Bins)
        {
            total += count;
        }

        Assert.Equal(pixels.Length, total);
    }

    [Theory(DisplayName = "히스토그램: 값이 들어가는 구간 (14-bit, 256구간 → 구간 폭 64)")]
    [InlineData(0, 0)]
    [InlineData(63, 0)]
    [InlineData(64, 1)]
    [InlineData(8192, 128)]
    [InlineData(16383, 255)]
    [InlineData(65535, 255)] // 범위를 넘는 값은 마지막 구간
    public void Histogram_BinPlacement(ushort value, int expectedBin)
    {
        Histogram histogram = Histogram.Compute([value], maxValue: 16383, binCount: 256);

        Assert.Equal(1, histogram.Bins[expectedBin]);
        Assert.Equal(expectedBin, histogram.BinOf(value));
    }
}
