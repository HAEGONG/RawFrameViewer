using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RawFrameViewer.Core;
using RawFrameViewer.Core.Protocol;

namespace RawFrameViewer.Simulator;

/// <summary>
/// 가상 검출기. TCP로 대기하다 뷰어가 접속하면 프레임을 보낸다. docs/protocol.md §1, §5
/// </summary>
internal sealed class DetectorServer(SimulatorOptions options)
{
    /// <summary>보낼 것이 없을 때 Heartbeat를 보내는 간격. §5 ③</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly PatternGenerator _generator = new(options.Width, options.Height, options.BitsStored);

    private readonly Lock _sessionGate = new();

    /// <summary>현재 연결된 뷰어의 전송을 멈추는 데 쓰는 토큰. 연결이 없으면 null. §1</summary>
    private CancellationTokenSource? _currentSession;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 포트폴리오 데모용이므로 같은 PC에서만 접속을 받는다.
        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        listener.Start();
        Log($"대기 중: 127.0.0.1:{options.Port} | {options.Width}×{options.Height}, {options.BitsStored}-bit, {options.Fps} fps | 메시지 {MessageEncoder.GetMessageLength((uint)(options.Width * options.Height * 2)):N0} bytes");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

                // 이미 연결된 뷰어가 있으면 그 연결을 닫고 새 뷰어로 교체한다. §1
                // 뷰어가 끊긴 직후 바로 재접속하면, 시뮬레이터는 이전 연결이 끊긴 것을 다음 전송 때에야 알게 된다.
                // 새 접속을 거부하면 이 사이에 재접속한 뷰어가 거부되므로, 최신 접속을 우선한다.
                var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_sessionGate)
                {
                    if (_currentSession is not null)
                    {
                        Log($"기존 연결을 닫고 새 뷰어로 교체: {client.Client.RemoteEndPoint}");
                        _currentSession.Cancel();
                    }

                    _currentSession = session;
                }

                // 전송은 별도 작업으로 돌려, 그동안에도 새 접속을 받을 수 있게 한다.
                _ = ServeAsync(client, session);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 종료 요청
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationTokenSource session)
    {
        EndPoint? remote = client.Client.RemoteEndPoint;
        Log($"뷰어 연결됨: {remote}");

        try
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                await StreamFramesAsync(stream, session.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (session.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException)
        {
            // 새 뷰어로 교체되었거나 프로그램 종료 요청
            Log($"연결 종료: {remote}");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Log($"뷰어 연결 끊김: {remote} ({ex.GetBaseException().Message})");
        }
        finally
        {
            // 이미 다른 뷰어로 교체되었다면 그 연결의 토큰은 건드리지 않는다.
            lock (_sessionGate)
            {
                if (ReferenceEquals(_currentSession, session))
                {
                    _currentSession = null;
                }

                session.Dispose();
            }
        }
    }

    private async Task StreamFramesAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // FrameId는 연결마다 1부터 시작한다. §3.1
        uint frameId = 0;
        bool sendFrames = options.Fps > 0;
        TimeSpan frameInterval = sendFrames ? TimeSpan.FromSeconds(1 / options.Fps) : Timeout.InfiniteTimeSpan;

        var clock = Stopwatch.StartNew();
        TimeSpan nextFrameAt = TimeSpan.Zero;
        TimeSpan lastSentAt = TimeSpan.Zero;

        // 1초마다 전송 통계를 출력한다.
        TimeSpan nextReportAt = TimeSpan.FromSeconds(1);
        int framesSinceReport = 0;
        long bytesSinceReport = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan now = clock.Elapsed;

            if (sendFrames && now >= nextFrameAt)
            {
                frameId++;
                // 영상을 다 만든 뒤에 시각을 찍는다. 인자 안에서 함께 호출하면 시각이 먼저 계산되어
                // 영상 생성 시간까지 지연 시간에 섞여 들어간다.
                ushort[] pixels = _generator.Next(frameId);
                var frame = new RawFrame(frameId, NowUnixMicroseconds(), options.Width, options.Height, options.BitsStored, pixels);
                byte[] message = MessageEncoder.EncodeFrame(frame);
                await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);

                lastSentAt = clock.Elapsed;
                framesSinceReport++;
                bytesSinceReport += message.Length;

                // 전송이 FPS를 따라가지 못하면 밀린 프레임을 몰아 보내지 않고 기준을 다시 잡는다.
                nextFrameAt += frameInterval;
                if (nextFrameAt < lastSentAt)
                {
                    nextFrameAt = lastSentAt + frameInterval;
                }
            }
            else if (now - lastSentAt >= HeartbeatInterval)
            {
                byte[] heartbeat = MessageEncoder.EncodeHeartbeat(frameId, NowUnixMicroseconds());
                await stream.WriteAsync(heartbeat, cancellationToken).ConfigureAwait(false);
                lastSentAt = clock.Elapsed;
                Log($"Heartbeat 전송 (마지막 FrameId {frameId})");
            }

            if (now >= nextReportAt)
            {
                if (framesSinceReport > 0)
                {
                    Log($"전송 {framesSinceReport}장, {bytesSinceReport / 1024.0 / 1024.0:F1} MB/s, 마지막 FrameId {frameId}");
                }

                framesSinceReport = 0;
                bytesSinceReport = 0;
                nextReportAt += TimeSpan.FromSeconds(1);
            }

            // 다음에 할 일(프레임 또는 Heartbeat)까지 기다린다.
            TimeSpan nextEvent = lastSentAt + HeartbeatInterval;
            if (sendFrames && nextFrameAt < nextEvent)
            {
                nextEvent = nextFrameAt;
            }

            TimeSpan wait = nextEvent - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static long NowUnixMicroseconds() =>
        (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
