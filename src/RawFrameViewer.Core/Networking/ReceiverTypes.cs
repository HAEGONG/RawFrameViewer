namespace RawFrameViewer.Core.Networking;

/// <summary>수신기 연결 상태</summary>
public enum ConnectionState
{
    Stopped,
    Connecting,
    Connected,
    /// <summary>연결 실패나 끊김 후 재연결을 기다리는 중. §6.5</summary>
    WaitingToReconnect,
}

/// <summary>수신 중 발생한 이벤트 종류. 명세 §7 표의 "기록 이벤트"와 이름을 맞춘다.</summary>
public enum ReceiverEventKind
{
    Connected,
    ConnectFailed,
    Disconnected,
    ConnectionTimeout,
    ProtocolError,
    SyncLost,
    PayloadCrcError,
    FramesMissed,
    DuplicateFrame,
    PixelOutOfRange,
}

public enum ReceiverEventLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>화면 로그에 표시할 수신 이벤트 하나</summary>
public sealed record ReceiverEvent(DateTime Time, ReceiverEventKind Kind, ReceiverEventLevel Level, string Message);

/// <summary>수신 통계의 한 시점 사본. 화면은 이 값을 주기적으로 읽어 표시한다.</summary>
public sealed record ReceiverStatistics(
    long FramesReceived,
    long FramesMissed,
    long PayloadCrcErrors,
    long SyncLostCount,
    long SkippedBytes,
    long DuplicateFrames,
    long PixelOutOfRangeFrames,
    long Timeouts,
    long Reconnects,
    double LastLatencyMs);

/// <summary>수신기 설정. 기본값은 명세 §6.5를 따른다. 테스트에서는 짧은 값으로 바꿔 쓴다.</summary>
public sealed record ReceiverOptions
{
    /// <summary>마지막 메시지 이후 이 시간 동안 아무것도 오지 않으면 연결이 죽은 것으로 본다.</summary>
    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>재연결 대기 시간. 마지막 값은 이후 계속 반복된다. 연결에 성공하면 처음으로 돌아간다.</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)];
}
