using System.Net;
using System.Net.Sockets;
using RawFrameViewer.Core.Protocol;
using RawFrameViewer.Core.Validation;

namespace RawFrameViewer.Core.Networking;

/// <summary>
/// 시뮬레이터에 접속해 프레임을 받는다. 연결 감시·재연결(§6.5)과 이상 감지(§6.2~§6.4)를 담당한다.
/// UI에 의존하지 않으므로 단위 테스트할 수 있다.
/// </summary>
/// <remarks>
/// 이벤트는 수신용 백그라운드 스레드에서 발생한다. 화면을 바꾸려면 받는 쪽에서 UI 스레드로 넘겨야 한다.
/// </remarks>
public sealed class FrameReceiver : IAsyncDisposable
{
    private const int StreamBufferSize = 64 * 1024;

    private readonly string _host;
    private readonly int _port;
    private readonly ReceiverOptions _options;
    private readonly FrameSequenceTracker _sequence = new();
    private readonly Lock _stateGate = new();

    private CancellationTokenSource? _stopSource;
    private Task? _loop;
    private ConnectionState _state = ConnectionState.Stopped;

    // 통계 카운터. 수신 스레드가 쓰고 UI 스레드가 읽으므로 Interlocked로 다룬다.
    private long _framesReceived;
    private long _framesMissed;
    private long _payloadCrcErrors;
    private long _syncLostCount;
    private long _skippedBytes;
    private long _duplicateFrames;
    private long _pixelOutOfRangeFrames;
    private long _timeouts;
    private long _reconnects;
    private long _lastLatencyUs;

    public FrameReceiver(string host, int port, ReceiverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // 범위를 벗어난 포트는 연결 시도 중에 SocketException이 아닌 다른 예외를 내서 수신 루프가 조용히 멈춘다.
        // 생성 시점에 막는다. (docs/ai-log.md #7)
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        _host = host;
        _port = port;
        _options = options ?? new ReceiverOptions();
    }

    /// <summary>정상 프레임 수신. 누락·중복·CRC 오류 프레임은 여기로 오지 않는다.</summary>
    public event Action<RawFrame>? FrameReceived;

    public event Action<ConnectionState>? StateChanged;

    public event Action<ReceiverEvent>? EventRaised;

    public ConnectionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public ReceiverStatistics GetStatistics() => new(
        Interlocked.Read(ref _framesReceived),
        Interlocked.Read(ref _framesMissed),
        Interlocked.Read(ref _payloadCrcErrors),
        Interlocked.Read(ref _syncLostCount),
        Interlocked.Read(ref _skippedBytes),
        Interlocked.Read(ref _duplicateFrames),
        Interlocked.Read(ref _pixelOutOfRangeFrames),
        Interlocked.Read(ref _timeouts),
        Interlocked.Read(ref _reconnects),
        Interlocked.Read(ref _lastLatencyUs) / 1000.0);

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("이미 시작했습니다.");
        }

        _stopSource = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stopSource.Token));
    }

    public async Task StopAsync()
    {
        if (_loop is null || _stopSource is null)
        {
            return;
        }

        await _stopSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }

        _stopSource.Dispose();
        _stopSource = null;
        _loop = null;
        SetState(ConnectionState.Stopped);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stopToken)
    {
        int attempt = 0;
        bool connectedBefore = false;

        while (!stopToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            TcpClient? client = await TryConnectAsync(stopToken).ConfigureAwait(false);

            if (client is not null)
            {
                attempt = 0; // 연결에 성공하면 재연결 간격을 처음으로 되돌린다. §6.5
                if (connectedBefore)
                {
                    Interlocked.Increment(ref _reconnects);
                }

                connectedBefore = true;
                using (client)
                {
                    await ReceiveUntilDisconnectedAsync(client, stopToken).ConfigureAwait(false);
                }
            }

            if (stopToken.IsCancellationRequested)
            {
                break;
            }

            TimeSpan delay = _options.ReconnectDelays[Math.Min(attempt, _options.ReconnectDelays.Count - 1)];
            attempt++;
            SetState(ConnectionState.WaitingToReconnect);
            await Task.Delay(delay, stopToken).ConfigureAwait(false);
        }
    }

    private async Task<TcpClient?> TryConnectAsync(CancellationToken stopToken)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        timeout.CancelAfter(_options.ConnectTimeout);

        try
        {
            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (!stopToken.IsCancellationRequested && ex is SocketException or OperationCanceledException)
        {
            client.Dispose();
            string reason = ex is OperationCanceledException ? "시간 초과" : ex.Message;
            Raise(ReceiverEventKind.ConnectFailed, ReceiverEventLevel.Warning, $"{_host}:{_port} 연결 실패 ({reason})");
            return null;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task ReceiveUntilDisconnectedAsync(TcpClient client, CancellationToken stopToken)
    {
        // FrameId는 연결마다 1부터 다시 시작한다. §6.3
        _sequence.Reset();
        SetState(ConnectionState.Connected);
        Raise(ReceiverEventKind.Connected, ReceiverEventLevel.Info, $"{_host}:{_port} 연결됨");

        var reader = new MessageReader(new BufferedStream(client.GetStream(), StreamBufferSize));
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                ReceivedMessage? message = await ReadWithTimeoutAsync(reader, stopToken).ConfigureAwait(false);
                if (message is null)
                {
                    Raise(ReceiverEventKind.Disconnected, ReceiverEventLevel.Warning, "상대가 연결을 닫음");
                    return;
                }

                Handle(message);
            }
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref _timeouts);
            Raise(ReceiverEventKind.ConnectionTimeout, ReceiverEventLevel.Error, $"{_options.ReceiveTimeout.TotalSeconds:0.#}초 동안 수신 없음 → 연결 종료 후 재연결");
        }
        catch (ProtocolException ex)
        {
            Raise(ReceiverEventKind.ProtocolError, ReceiverEventLevel.Error, $"프로토콜 오류({ex.Kind}): {ex.Message}");
        }
        catch (Exception ex) when (!stopToken.IsCancellationRequested && ex is IOException or SocketException)
        {
            Raise(ReceiverEventKind.Disconnected, ReceiverEventLevel.Warning, $"연결 끊김 ({ex.GetBaseException().Message})");
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // 사용자가 중지함
        }
#pragma warning disable CA1031 // 수신 루프는 어떤 예외에도 멈추지 않고 재연결해야 한다. 원인은 로그로 남긴다.
        catch (Exception ex) when (!stopToken.IsCancellationRequested)
        {
            Raise(ReceiverEventKind.ProtocolError, ReceiverEventLevel.Error, $"예상치 못한 오류로 연결 종료: {ex.GetType().Name}: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>메시지 하나를 읽는다. 제한 시간 안에 못 읽으면 TimeoutException. §6.5</summary>
    private async Task<ReceivedMessage?> ReadWithTimeoutAsync(MessageReader reader, CancellationToken stopToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        timeout.CancelAfter(_options.ReceiveTimeout);
        try
        {
            return await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private void Handle(ReceivedMessage message)
    {
        if (message.SkippedBytes > 0)
        {
            Interlocked.Increment(ref _syncLostCount);
            Interlocked.Add(ref _skippedBytes, message.SkippedBytes);
            Raise(ReceiverEventKind.SyncLost, ReceiverEventLevel.Warning, $"메시지 경계를 잃음 → {message.SkippedBytes:N0}바이트 건너뛰고 재동기화");
        }

        switch (message.Kind)
        {
            case ReceiveKind.PayloadCrcError:
                Interlocked.Increment(ref _payloadCrcErrors);
                Raise(ReceiverEventKind.PayloadCrcError, ReceiverEventLevel.Warning, $"Frame #{message.Header.FrameId} 영상 CRC 불일치 → 폐기");

                // 헤더는 정상이므로 이 번호의 프레임이 "도착은 했다"는 것을 안다.
                // 번호를 반영하지 않으면 다음 프레임에서 같은 프레임이 누락으로 한 번 더 세어진다. §6.3
                if (message.Header.MessageType == MessageType.Frame)
                {
                    ReportMissed(_sequence.OnFrame(message.Header.FrameId).MissedCount, $"Frame #{message.Header.FrameId}");
                }

                break;

            case ReceiveKind.Heartbeat:
                ReportMissed(_sequence.OnHeartbeat(message.Header.FrameId), "Heartbeat");
                break;

            case ReceiveKind.Frame:
                HandleFrame(message.Frame!);
                break;
        }
    }

    private void HandleFrame(RawFrame frame)
    {
        SequenceResult sequence = _sequence.OnFrame(frame.FrameId);
        if (sequence.Verdict == SequenceVerdict.Duplicate)
        {
            Interlocked.Increment(ref _duplicateFrames);
            Raise(ReceiverEventKind.DuplicateFrame, ReceiverEventLevel.Warning, $"Frame #{frame.FrameId} 중복 또는 순서 뒤바뀜 → 폐기");
            return;
        }

        ReportMissed(sequence.MissedCount, $"Frame #{frame.FrameId}");

        int outOfRange = PixelRangeChecker.CountOutOfRange(frame.Pixels, frame.BitsStored);
        if (outOfRange > 0)
        {
            Interlocked.Increment(ref _pixelOutOfRangeFrames);
            Raise(ReceiverEventKind.PixelOutOfRange, ReceiverEventLevel.Warning, $"Frame #{frame.FrameId}: {outOfRange:N0}개 픽셀이 {frame.BitsStored}-bit 범위(0~{frame.MaxValue}) 초과");
        }

        long nowUs = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;
        Interlocked.Exchange(ref _lastLatencyUs, nowUs - frame.TimestampUs);
        Interlocked.Increment(ref _framesReceived);
        FrameReceived?.Invoke(frame);
    }

    private void ReportMissed(uint missed, string detectedBy)
    {
        if (missed == 0)
        {
            return;
        }

        Interlocked.Add(ref _framesMissed, missed);
        Raise(ReceiverEventKind.FramesMissed, ReceiverEventLevel.Warning, $"{missed}개 프레임 누락 ({detectedBy}에서 확인)");
    }

    private void SetState(ConnectionState state)
    {
        lock (_stateGate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(state);
    }

    private void Raise(ReceiverEventKind kind, ReceiverEventLevel level, string message) =>
        EventRaised?.Invoke(new ReceiverEvent(DateTime.Now, kind, level, message));
}
