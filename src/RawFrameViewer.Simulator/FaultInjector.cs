using System.Collections.Concurrent;
using System.Globalization;

namespace RawFrameViewer.Simulator;

/// <summary>오류 주입 종류. 번호는 docs/protocol.md §7 표의 ID(F1~F6)와 같다.</summary>
internal enum FaultKind
{
    DropFrame = 1,
    CorruptPayload = 2,
    CorruptHeader = 3,
    Disconnect = 4,
    Stall = 5,
    PixelOutOfRange = 6,
}

/// <summary>
/// 다음에 보낼 프레임에 적용할 오류를 관리한다.
/// 오류는 두 가지 방법으로 요청한다.
/// - 키보드(1~6): 시연 중 원하는 순간에 하나씩
/// - 일정(--inject F1@20,F2@40): FrameId 기준으로 정해 둔 순서대로. 연결마다 FrameId가 1부터 다시 시작하므로 연결할 때마다 반복된다.
/// </summary>
internal sealed class FaultInjector(IReadOnlyDictionary<uint, IReadOnlyList<FaultKind>> schedule)
{
    private readonly ConcurrentQueue<FaultKind> _requested = new();

    public static FaultInjector None { get; } = new(new Dictionary<uint, IReadOnlyList<FaultKind>>());

    public static string Describe(FaultKind kind) => kind switch
    {
        FaultKind.DropFrame => "F1 프레임 누락",
        FaultKind.CorruptPayload => "F2 영상 데이터 손상",
        FaultKind.CorruptHeader => "F3 헤더 손상",
        FaultKind.Disconnect => "F4 연결 끊기",
        FaultKind.Stall => "F5 전송 정지",
        FaultKind.PixelOutOfRange => "F6 범위 초과 픽셀",
        _ => kind.ToString(),
    };

    /// <summary>키보드 등으로 다음 프레임에 오류를 하나 요청한다.</summary>
    public void Request(FaultKind kind) => _requested.Enqueue(kind);

    /// <summary>이 프레임에 적용할 오류를 꺼낸다. 요청된 것과 일정에 있는 것을 합친다.</summary>
    public HashSet<FaultKind> TakeFor(uint frameId)
    {
        var faults = new HashSet<FaultKind>();
        while (_requested.TryDequeue(out FaultKind kind))
        {
            faults.Add(kind);
        }

        if (schedule.TryGetValue(frameId, out IReadOnlyList<FaultKind>? scheduled))
        {
            faults.UnionWith(scheduled);
        }

        return faults;
    }

    /// <summary>"F1@20,F2@40" 형식의 일정을 해석한다.</summary>
    public static FaultInjector? Parse(string text, out string? error)
    {
        error = null;
        var schedule = new Dictionary<uint, List<FaultKind>>();

        foreach (string item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = item.Split('@');
            if (parts.Length != 2
                || parts[0].Length != 2
                || char.ToUpperInvariant(parts[0][0]) != 'F'
                || !int.TryParse(parts[0].AsSpan(1), CultureInfo.InvariantCulture, out int id)
                || !Enum.IsDefined((FaultKind)id)
                || !uint.TryParse(parts[1], CultureInfo.InvariantCulture, out uint frameId)
                || frameId == 0)
            {
                error = $"오류 주입 일정 형식이 올바르지 않습니다: '{item}' (예: F1@20,F2@40)";
                return null;
            }

            if (!schedule.TryGetValue(frameId, out List<FaultKind>? list))
            {
                schedule[frameId] = list = [];
            }

            list.Add((FaultKind)id);
        }

        return new FaultInjector(schedule.ToDictionary(p => p.Key, p => (IReadOnlyList<FaultKind>)p.Value));
    }
}
