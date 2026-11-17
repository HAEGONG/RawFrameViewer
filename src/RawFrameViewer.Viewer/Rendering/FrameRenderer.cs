using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawFrameViewer.Core;
using RawFrameViewer.Core.Imaging;

namespace RawFrameViewer.Viewer.Rendering;

/// <summary>
/// 16-bit 프레임을 화면용 8-bit 회색조 비트맵으로 그린다.
/// 비트맵은 영상 크기가 바뀔 때만 새로 만들고, 평소에는 같은 비트맵에 픽셀만 덮어쓴다.
/// UI 스레드에서만 호출해야 한다.
/// </summary>
internal sealed class FrameRenderer
{
    private byte[] _gray = [];

    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>마지막 렌더링에 걸린 시간(ms). 성능 표시용.</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>프레임을 그린다.</summary>
    /// <returns>비트맵을 새로 만들었으면 true. 화면이 새 비트맵을 가리키도록 알려야 한다.</returns>
    public bool Render(RawFrame frame, byte[] lut)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool recreated = false;

        if (Bitmap is null || Bitmap.PixelWidth != frame.Width || Bitmap.PixelHeight != frame.Height)
        {
            Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Gray8, null);
            _gray = new byte[frame.Width * frame.Height];
            recreated = true;
        }

        WindowLevel.Apply(frame.Pixels, _gray, lut);
        Bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), _gray, frame.Width, 0);

        LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return recreated;
    }
}
