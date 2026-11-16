namespace RawFrameViewer.Core.Validation;

/// <summary>BitsStored로 표현할 수 없는 값을 가진 픽셀을 센다. docs/protocol.md §6.4</summary>
public static class PixelRangeChecker
{
    public static int CountOutOfRange(ReadOnlySpan<ushort> pixels, byte bitsStored)
    {
        if (bitsStored is 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsStored), "BitsStored는 1~16이어야 합니다.");
        }

        if (bitsStored == 16)
        {
            return 0;
        }

        ushort max = (ushort)((1 << bitsStored) - 1);

        // 대부분의 프레임은 정상이므로 먼저 벡터화된 검색으로 범위 밖 값이 있는지만 빠르게 확인한다.
        int first = pixels.IndexOfAnyExceptInRange((ushort)0, max);
        if (first < 0)
        {
            return 0;
        }

        int count = 0;
        foreach (ushort value in pixels[first..])
        {
            if (value > max)
            {
                count++;
            }
        }

        return count;
    }
}
