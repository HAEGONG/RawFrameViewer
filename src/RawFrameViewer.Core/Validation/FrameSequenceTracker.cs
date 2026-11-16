namespace RawFrameViewer.Core.Validation;

/// <summary>FrameId 검사 결과. §6.3</summary>
public enum SequenceVerdict
{
    /// <summary>표시해도 되는 프레임 (누락이 있었을 수 있음)</summary>
    Accepted,
    /// <summary>이미 받은 번호이거나 순서가 뒤바뀐 프레임 → 폐기</summary>
    Duplicate,
}

/// <param name="MissedCount">이 프레임 앞에서 빠진 프레임 수</param>
public readonly record struct SequenceResult(SequenceVerdict Verdict, uint MissedCount);

/// <summary>
/// 연결 하나 동안 FrameId 흐름을 추적해 누락·중복을 찾는다. docs/protocol.md §6.3
/// 송신 측 FrameId는 연결마다 1부터 시작하므로, 기준값은 0에서 출발한다.
/// </summary>
public sealed class FrameSequenceTracker
{
    /// <summary>마지막으로 확인한 FrameId. 0이면 아직 받은 것이 없다.</summary>
    public uint LastFrameId { get; private set; }

    /// <summary>새 연결이 맺어지면 호출한다.</summary>
    public void Reset() => LastFrameId = 0;

    public SequenceResult OnFrame(uint frameId)
    {
        if (frameId <= LastFrameId)
        {
            return new SequenceResult(SequenceVerdict.Duplicate, 0);
        }

        uint missed = frameId - LastFrameId - 1;
        LastFrameId = frameId;
        return new SequenceResult(SequenceVerdict.Accepted, missed);
    }

    /// <summary>
    /// Heartbeat에 담긴 "마지막으로 보낸 FrameId"를 반영한다.
    /// 그 값이 지금까지 받은 것보다 크면 그 사이 프레임은 누락된 것이다.
    /// </summary>
    /// <returns>새로 확인된 누락 수</returns>
    public uint OnHeartbeat(uint lastSentFrameId)
    {
        if (lastSentFrameId <= LastFrameId)
        {
            return 0;
        }

        uint missed = lastSentFrameId - LastFrameId;
        LastFrameId = lastSentFrameId;
        return missed;
    }
}
