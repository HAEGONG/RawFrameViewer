using RawFrameViewer.Core;

namespace RawFrameViewer.Core.Tests;

/// <summary>테스트용 프레임 생성 도우미</summary>
internal static class TestFrames
{
    /// <summary>
    /// 픽셀마다 다른 값을 가진 프레임을 만든다. 값이 모두 같으면 바이트 순서 오류를 놓칠 수 있기 때문이다.
    /// 값은 0x0102처럼 상위·하위 바이트가 서로 다르게 나오도록 만든다.
    /// </summary>
    public static RawFrame Create(uint frameId = 1, ushort width = 4, ushort height = 3, byte bitsStored = 14)
    {
        ushort max = (ushort)((1 << bitsStored) - 1);
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)((0x0102 + (i * 0x0101)) % (max + 1));
        }

        return new RawFrame(frameId, timestampUs: 1_700_000_000_000_000 + frameId, width, height, bitsStored, pixels);
    }
}
