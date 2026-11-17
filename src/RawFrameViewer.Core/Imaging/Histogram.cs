namespace RawFrameViewer.Core.Imaging;

/// <summary>픽셀 값 분포. 0~maxValue 범위를 같은 폭의 구간(bin)으로 나눠 센다.</summary>
public sealed class Histogram
{
    private readonly int[] _bins;

    private Histogram(int[] bins, int maxValue)
    {
        _bins = bins;
        MaxValue = maxValue;
    }

    /// <summary>히스토그램이 다루는 값의 최댓값. 이보다 큰 값은 마지막 구간에 넣는다.</summary>
    public int MaxValue { get; }

    public ReadOnlySpan<int> Bins => _bins;

    public int BinCount => _bins.Length;

    /// <summary>값 하나가 들어가는 구간 번호</summary>
    public int BinOf(double value) => (int)Math.Clamp(value * BinCount / (MaxValue + 1.0), 0, BinCount - 1);

    public static Histogram Compute(ReadOnlySpan<ushort> pixels, int maxValue, int binCount = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxValue);

        var bins = new int[binCount];

        // 값 → 구간 변환표를 먼저 만들어 픽셀마다 나눗셈하지 않는다. 범위를 넘는 값은 마지막 구간.
        var binOfValue = new int[ushort.MaxValue + 1];
        for (int value = 0; value < binOfValue.Length; value++)
        {
            binOfValue[value] = (int)Math.Min((long)value * binCount / (maxValue + 1), binCount - 1);
        }

        foreach (ushort value in pixels)
        {
            bins[binOfValue[value]]++;
        }

        return new Histogram(bins, maxValue);
    }
}
