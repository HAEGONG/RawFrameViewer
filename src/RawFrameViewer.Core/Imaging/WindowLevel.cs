namespace RawFrameViewer.Core.Imaging;

/// <summary>
/// 16-bit 값을 화면용 8-bit(0~255)로 바꾸는 Window/Level 설정.
/// Center(Level)를 중간 회색으로, 그 위아래 Width(Window) 범위를 0~255에 펼친다.
/// 범위보다 어두운 값은 0(검정), 밝은 값은 255(흰색)로 잘린다.
/// </summary>
/// <example>Center=8000, Width=2000 → 7000 이하는 0, 8000은 128, 9000 이상은 255</example>
public readonly record struct WindowLevel(double Center, double Width)
{
    /// <summary>Width가 0이면 나눗셈이 불가능하므로 최소 1로 취급한다.</summary>
    public const double MinWidth = 1.0;

    private const int LutSize = ushort.MaxValue + 1;

    /// <summary>값의 최소·최대 범위 전체가 보이도록 하는 설정을 만든다.</summary>
    public static WindowLevel FromRange(ushort min, ushort max)
    {
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return new WindowLevel((min + max) / 2.0, Math.Max(max - min, MinWidth));
    }

    /// <summary>
    /// 65536칸 변환표(LUT)를 만든다. 픽셀마다 계산하는 대신 표를 한 번 만들어 두고 찾아 쓴다.
    /// </summary>
    /// <param name="invert">true면 밝기를 반전한다.</param>
    public byte[] BuildLut(bool invert = false)
    {
        if (!double.IsFinite(Center) || !double.IsFinite(Width))
        {
            throw new InvalidOperationException($"Window/Level 값이 올바르지 않습니다. (Center={Center}, Width={Width})");
        }

        double width = Math.Max(Width, MinWidth);
        double lower = Center - (width / 2.0);

        byte[] lut = new byte[LutSize];
        for (int value = 0; value < LutSize; value++)
        {
            double ratio = Math.Clamp((value - lower) / width, 0.0, 1.0);
            byte gray = (byte)Math.Round(ratio * 255.0, MidpointRounding.AwayFromZero);
            lut[value] = invert ? (byte)(255 - gray) : gray;
        }

        return lut;
    }

    /// <summary>LUT를 이용해 16-bit 픽셀을 8-bit로 변환한다.</summary>
    public static void Apply(ReadOnlySpan<ushort> source, Span<byte> destination, ReadOnlySpan<byte> lut)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("출력 버퍼가 입력보다 작습니다.", nameof(destination));
        }

        if (lut.Length != LutSize)
        {
            throw new ArgumentException($"LUT 길이는 {LutSize}이어야 합니다.", nameof(lut));
        }

        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = lut[source[i]];
        }
    }
}
