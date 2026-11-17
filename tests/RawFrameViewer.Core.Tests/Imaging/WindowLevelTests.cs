using RawFrameViewer.Core.Imaging;

namespace RawFrameViewer.Core.Tests.Imaging;

public class WindowLevelTests
{
    [Theory(DisplayName = "Center=8000, Width=2000 예시값")]
    [InlineData(0, 0)]
    [InlineData(6500, 0)]     // 범위보다 어두움 → 검정
    [InlineData(7000, 0)]     // 하한
    [InlineData(8000, 128)]   // 중심 → 중간 회색 (127.5 반올림)
    [InlineData(9000, 255)]   // 상한
    [InlineData(9500, 255)]   // 범위보다 밝음 → 흰색
    [InlineData(65535, 255)]
    public void Lut_ExampleValues(int value, byte expected)
    {
        byte[] lut = new WindowLevel(8000, 2000).BuildLut();

        Assert.Equal(expected, lut[value]);
    }

    [Fact(DisplayName = "값이 커지면 밝기는 줄어들지 않는다")]
    public void Lut_IsMonotonic()
    {
        byte[] lut = new WindowLevel(8000, 2000).BuildLut();

        for (int i = 1; i < lut.Length; i++)
        {
            Assert.True(lut[i] >= lut[i - 1], $"{i}에서 감소");
        }
    }

    [Fact(DisplayName = "반전하면 255에서 뺀 값")]
    public void Lut_Invert()
    {
        byte[] normal = new WindowLevel(8000, 2000).BuildLut();
        byte[] inverted = new WindowLevel(8000, 2000).BuildLut(invert: true);

        for (int i = 0; i < normal.Length; i++)
        {
            Assert.Equal(255 - normal[i], inverted[i]);
        }
    }

    [Theory(DisplayName = "Width가 0이나 음수여도 0으로 나누지 않는다")]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    public void Lut_ZeroOrNegativeWidth_DoesNotThrow(double width)
    {
        byte[] lut = new WindowLevel(1000, width).BuildLut();

        Assert.Equal(0, lut[999]);
        Assert.Equal(255, lut[1001]);
    }

    [Theory(DisplayName = "NaN·무한대 값은 거부한다")]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void Lut_NonFinite_Throws(double center, double width)
    {
        Assert.Throws<InvalidOperationException>(() => new WindowLevel(center, width).BuildLut());
    }

    [Fact(DisplayName = "FromRange는 최소~최대를 0~255에 펼친다")]
    public void FromRange_CoversMinToMax()
    {
        byte[] lut = WindowLevel.FromRange(1000, 5000).BuildLut();

        Assert.Equal(0, lut[1000]);
        Assert.Equal(255, lut[5000]);
    }

    [Fact(DisplayName = "MinMax는 최솟값과 최댓값을 찾는다")]
    public void MinMax_FindsExtremes()
    {
        ushort[] pixels = [500, 12, 16000, 7];

        Assert.Equal(((ushort)7, (ushort)16000), PixelStatistics.MinMax(pixels));
    }

    [Fact(DisplayName = "MinMax는 빈 입력을 거부한다")]
    public void MinMax_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => PixelStatistics.MinMax([]));
    }

    [Fact(DisplayName = "Apply는 LUT로 픽셀을 변환한다")]
    public void Apply_UsesLut()
    {
        byte[] lut = new WindowLevel(8000, 2000).BuildLut();
        ushort[] source = [7000, 8000, 9000];
        byte[] destination = new byte[3];

        WindowLevel.Apply(source, destination, lut);

        Assert.Equal(new byte[] { 0, 128, 255 }, destination);
    }
}
