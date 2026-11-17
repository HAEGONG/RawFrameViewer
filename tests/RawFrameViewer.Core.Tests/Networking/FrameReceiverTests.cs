using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RawFrameViewer.Core.Networking;
using RawFrameViewer.Core.Protocol;

namespace RawFrameViewer.Core.Tests.Networking;

/// <summary>
/// 실제 TCP 소켓(127.0.0.1)으로 수신기를 검증한다. 메모리 스트림으로는 드러나지 않는
/// 연결·끊김·타임아웃 동작을 확인하기 위해서다. (docs/ai-log.md #2 참고)
/// </summary>
public sealed class FrameReceiverTests : IDisposable
{
    /// <summary>테스트가 오래 걸리지 않도록 타임아웃과 재연결 간격을 짧게 잡는다.</summary>
    private static readonly ReceiverOptions FastOptions = new()
    {
        ReceiveTimeout = TimeSpan.FromMilliseconds(300),
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
    };

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<ReceiverEvent> _events = new();
    private readonly ConcurrentQueue<RawFrame> _frames = new();

    public FrameReceiverTests() => _listener.Start();

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose() => _listener.Stop();

    [Fact(DisplayName = "프레임을 받아 이벤트와 통계로 알린다")]
    public async Task ReceivesFrames()
    {
        await using FrameReceiver receiver = CreateReceiver();
        receiver.Start();

        using TcpClient server = await _listener.AcceptTcpClientAsync();
        await SendAsync(server, Frame(1), Frame(2), Frame(3));
        await WaitUntilAsync(() => _frames.Count == 3);

        Assert.Equal([1u, 2u, 3u], _frames.Select(f => f.FrameId));
        Assert.Contains(_events, e => e.Kind == ReceiverEventKind.Connected);
        Assert.Equal(3, receiver.GetStatistics().FramesReceived);
        Assert.Equal(ConnectionState.Connected, receiver.State);
    }

    [Fact(DisplayName = "§6.3 §7 누락·CRC 오류·중복을 구분해 기록하고 정상 프레임만 전달한다")]
    public async Task DetectsAnomalies()
    {
        await using FrameReceiver receiver = CreateReceiver();
        receiver.Start();

        byte[] corrupted = Frame(4);
        corrupted[ProtocolConstants.HeaderSize] ^= 0xFF;

        using TcpClient server = await _listener.AcceptTcpClientAsync();
        await SendAsync(server, Frame(1), Frame(3), corrupted, Frame(3), Frame(5));
        await WaitUntilAsync(() => _frames.Count == 3);

        ReceiverStatistics stats = receiver.GetStatistics();
        Assert.Equal([1u, 3u, 5u], _frames.Select(f => f.FrameId));
        Assert.Equal(1, stats.FramesMissed);       // 2만 누락. 4는 도착했지만 깨진 것이므로 CRC 오류로만 센다
        Assert.Equal(1, stats.PayloadCrcErrors);
        Assert.Equal(1, stats.DuplicateFrames);
    }

    [Fact(DisplayName = "§6.5 일정 시간 수신이 없으면 타임아웃 후 재연결한다")]
    public async Task SilentServer_TimesOutAndReconnects()
    {
        await using FrameReceiver receiver = CreateReceiver();
        receiver.Start();

        using TcpClient first = await _listener.AcceptTcpClientAsync(); // 아무것도 보내지 않음
        using TcpClient second = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(_events, e => e.Kind == ReceiverEventKind.ConnectionTimeout);
        Assert.Equal(1, receiver.GetStatistics().Timeouts);
    }

    [Fact(DisplayName = "§6.3 §6.5 끊긴 뒤 재연결하면 FrameId 1부터 다시 받는다")]
    public async Task ServerCloses_ReconnectsAndResetsSequence()
    {
        await using FrameReceiver receiver = CreateReceiver();
        receiver.Start();

        using (TcpClient first = await _listener.AcceptTcpClientAsync())
        {
            await SendAsync(first, Frame(1), Frame(2));
            await WaitUntilAsync(() => _frames.Count == 2);
        }

        using TcpClient second = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SendAsync(second, Frame(1));
        await WaitUntilAsync(() => _frames.Count == 3);

        ReceiverStatistics stats = receiver.GetStatistics();
        Assert.Equal(0, stats.DuplicateFrames); // 새 연결의 1번은 중복이 아니다
        Assert.Equal(1, stats.Reconnects);
        Assert.Contains(_events, e => e.Kind == ReceiverEventKind.Disconnected);
    }

    [Fact(DisplayName = "§6.5 서버가 없으면 연결 실패를 기록하고 계속 재시도한다")]
    public async Task NoServer_KeepsRetrying()
    {
        int port = Port;
        _listener.Stop(); // 아무도 듣고 있지 않은 포트

        await using var receiver = new FrameReceiver("127.0.0.1", port, FastOptions);
        receiver.EventRaised += _events.Enqueue;
        receiver.Start();

        await WaitUntilAsync(() => _events.Count(e => e.Kind == ReceiverEventKind.ConnectFailed) >= 2);
        Assert.NotEqual(ConnectionState.Connected, receiver.State);
    }

    [Theory(DisplayName = "포트가 1~65535를 벗어나면 생성 시점에 거부한다")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void InvalidPort_Throws(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameReceiver("127.0.0.1", port));
    }

    [Theory(DisplayName = "호스트가 비어 있으면 생성 시점에 거부한다")]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyHost_Throws(string host)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FrameReceiver(host, 5000));
    }

    [Fact(DisplayName = "Stop하면 Stopped 상태가 된다")]
    public async Task Stop_SetsStopped()
    {
        var receiver = CreateReceiver();
        receiver.Start();
        using TcpClient server = await _listener.AcceptTcpClientAsync();

        await receiver.StopAsync();

        Assert.Equal(ConnectionState.Stopped, receiver.State);
    }

    private FrameReceiver CreateReceiver()
    {
        var receiver = new FrameReceiver("127.0.0.1", Port, FastOptions);
        receiver.EventRaised += _events.Enqueue;
        receiver.FrameReceived += _frames.Enqueue;
        return receiver;
    }

    private static byte[] Frame(uint frameId) => MessageEncoder.EncodeFrame(TestFrames.Create(frameId));

    private static async Task SendAsync(TcpClient client, params byte[][] messages)
    {
        NetworkStream stream = client.GetStream();
        foreach (byte[] message in messages)
        {
            await stream.WriteAsync(message);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("조건이 5초 안에 만족되지 않았습니다.");
            }

            await Task.Delay(10);
        }
    }
}
