using RawFrameViewer.Core.Validation;

namespace RawFrameViewer.Core.Tests.Validation;

/// <summary>docs/protocol.md §6.3 FrameId 검사, §6.4 픽셀 범위 검사</summary>
public class ValidationTests
{
    [Fact(DisplayName = "§6.3 1, 2, 3 순서대로 오면 누락 없음")]
    public void Sequential_NoMissing()
    {
        var tracker = new FrameSequenceTracker();

        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 0), tracker.OnFrame(1));
        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 0), tracker.OnFrame(2));
        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 0), tracker.OnFrame(3));
    }

    [Fact(DisplayName = "§7 F1 3 다음 6이 오면 2장 누락, 6은 표시")]
    public void Gap_CountsMissing()
    {
        var tracker = new FrameSequenceTracker();
        tracker.OnFrame(3);

        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 2), tracker.OnFrame(6));
    }

    [Fact(DisplayName = "§6.3 첫 프레임이 1이 아니면 그 앞은 누락")]
    public void FirstFrameNotOne_CountsMissing()
    {
        var tracker = new FrameSequenceTracker();

        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 4), tracker.OnFrame(5));
    }

    [Theory(DisplayName = "§6.3 같은 번호나 이전 번호는 폐기")]
    [InlineData(5u)]
    [InlineData(4u)]
    public void SameOrOlder_IsDuplicate(uint frameId)
    {
        var tracker = new FrameSequenceTracker();
        tracker.OnFrame(5);

        Assert.Equal(SequenceVerdict.Duplicate, tracker.OnFrame(frameId).Verdict);
        Assert.Equal(5u, tracker.LastFrameId); // 기준값은 바뀌지 않는다
    }

    [Fact(DisplayName = "§6.3 Heartbeat가 더 큰 FrameId를 알려주면 그 차이만큼 누락")]
    public void Heartbeat_RevealsMissing()
    {
        var tracker = new FrameSequenceTracker();
        tracker.OnFrame(3);

        Assert.Equal(2u, tracker.OnHeartbeat(5));
        Assert.Equal(0u, tracker.OnHeartbeat(5));  // 같은 누락을 두 번 세지 않는다
        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 0), tracker.OnFrame(6));
    }

    [Fact(DisplayName = "§6.3 새 연결에서는 기준값을 초기화한다")]
    public void Reset_StartsOver()
    {
        var tracker = new FrameSequenceTracker();
        tracker.OnFrame(100);
        tracker.Reset();

        Assert.Equal(new SequenceResult(SequenceVerdict.Accepted, 0), tracker.OnFrame(1));
    }

    [Fact(DisplayName = "§6.4 BitsStored=14에서 16383 초과 픽셀만 센다")]
    public void PixelRange_CountsAboveMax()
    {
        ushort[] pixels = [0, 16383, 16384, 65535, 100, 16384];

        Assert.Equal(3, PixelRangeChecker.CountOutOfRange(pixels, 14));
    }

    [Fact(DisplayName = "§6.4 모두 범위 안이면 0")]
    public void PixelRange_AllValid_ReturnsZero()
    {
        ushort[] pixels = [0, 1, 16383];

        Assert.Equal(0, PixelRangeChecker.CountOutOfRange(pixels, 14));
    }

    [Fact(DisplayName = "§6.4 BitsStored=16이면 범위 초과가 있을 수 없다")]
    public void PixelRange_Bits16_AlwaysZero()
    {
        ushort[] pixels = [65535];

        Assert.Equal(0, PixelRangeChecker.CountOutOfRange(pixels, 16));
    }
}
