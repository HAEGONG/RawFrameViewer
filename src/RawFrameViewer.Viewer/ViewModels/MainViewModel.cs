using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RawFrameViewer.Core;
using RawFrameViewer.Core.Imaging;
using RawFrameViewer.Core.Networking;
using RawFrameViewer.Viewer.Rendering;

namespace RawFrameViewer.Viewer.ViewModels;

/// <summary>
/// 메인 화면의 상태와 동작. 화면(View)은 이 클래스의 속성에 바인딩만 한다.
/// </summary>
/// <remarks>
/// 스레드 규칙: FrameReceiver의 이벤트는 수신 스레드에서 오므로, 이 클래스의 속성을 바꾸기 전에
/// 반드시 Dispatcher로 UI 스레드에 넘긴다. WPF는 UI 스레드에서만 화면 객체를 바꿀 수 있다.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogEntries = 500;

    private readonly Dispatcher _dispatcher;
    private readonly FrameRenderer _renderer = new();
    private readonly DispatcherTimer _statisticsTimer;

    private FrameReceiver? _receiver;
    private RawFrame? _currentFrame;
    private byte[] _lut;

    // 수신 스레드 → UI 스레드 전달용. 화면이 따라가지 못하면 가장 최신 프레임만 그린다.
    private RawFrame? _pendingFrame;
    private int _renderQueued;

    private bool _autoWindowLevelPending = true;
    private bool _suspendLutUpdate;
    private long _framesDisplayed;
    private long _lastFramesReceived;
    private long _lastFramesDisplayed;
    private DateTime _lastStatisticsTime = DateTime.UtcNow;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _lut = new WindowLevel(WindowCenter, WindowWidth).BuildLut(Invert);

        // 통계는 프레임마다가 아니라 0.5초마다 한 번 읽어 화면 갱신 부담을 줄인다.
        _statisticsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshStatistics(), dispatcher);
        _statisticsTimer.Start();
    }

    // ── 연결 ─────────────────────────────────────────────

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStateText))]
    private ConnectionState _connectionState = ConnectionState.Stopped;

    public string ConnectButtonText => IsRunning ? "연결 끊기" : "연결";

    public string ConnectionStateText => ConnectionState switch
    {
        ConnectionState.Stopped => "중지됨",
        ConnectionState.Connecting => "연결 중…",
        ConnectionState.Connected => "연결됨",
        ConnectionState.WaitingToReconnect => "재연결 대기",
        _ => ConnectionState.ToString(),
    };

    // ── 영상과 Window/Level ─────────────────────────────

    [ObservableProperty]
    private ImageSource? _image;

    [ObservableProperty]
    private double _windowCenter = 8192;

    [ObservableProperty]
    private double _windowWidth = 16384;

    [ObservableProperty]
    private bool _invert;

    /// <summary>현재 영상의 BitsStored 기준 최댓값. 슬라이더 범위로 쓴다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxWindowWidth))]
    private double _maxPixelValue = 16383;

    public double MaxWindowWidth => MaxPixelValue + 1;

    [ObservableProperty]
    private string _frameInfo = "-";

    [ObservableProperty]
    private string _cursorInfo = string.Empty;

    // ── 통계 ─────────────────────────────────────────────

    [ObservableProperty]
    private double _receiveFps;

    [ObservableProperty]
    private double _displayFps;

    [ObservableProperty]
    private ReceiverStatistics? _statistics;

    [ObservableProperty]
    private double _renderMs;

    public ObservableCollection<LogEntry> Log { get; } = [];

    // ── 명령 ─────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (_receiver is null)
        {
            StartReceiver();
        }
        else
        {
            await StopReceiverAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void AutoWindowLevel()
    {
        if (_currentFrame is not null)
        {
            ApplyAutoWindowLevel(_currentFrame);
        }
    }

    /// <summary>마우스 오른쪽 드래그로 W/L을 조절한다. 가로 이동은 Window, 세로 이동은 Level.</summary>
    public void AdjustWindowLevel(double startCenter, double startWidth, double dx, double dy)
    {
        double sensitivity = MaxPixelValue / 512.0;
        SetWindowLevel(
            Math.Clamp(startCenter + (dy * sensitivity), 0, MaxPixelValue),
            Math.Clamp(startWidth + (dx * sensitivity), WindowLevel.MinWidth, MaxWindowWidth));
    }

    /// <summary>마우스 아래 픽셀의 위치와 원본 16-bit 값을 표시한다.</summary>
    public void UpdateCursor(double x, double y)
    {
        RawFrame? frame = _currentFrame;
        int px = (int)Math.Floor(x);
        int py = (int)Math.Floor(y);
        if (frame is null || px < 0 || py < 0 || px >= frame.Width || py >= frame.Height)
        {
            CursorInfo = string.Empty;
            return;
        }

        CursorInfo = $"X {px}, Y {py} : {frame.Pixels[(py * frame.Width) + px]}";
    }

    public async ValueTask DisposeAsync()
    {
        _statisticsTimer.Stop();
        await StopReceiverAsync().ConfigureAwait(false);
    }

    // ── W/L 변경 처리 ───────────────────────────────────

    partial void OnWindowCenterChanged(double value) => UpdateLutAndRender();

    partial void OnWindowWidthChanged(double value) => UpdateLutAndRender();

    partial void OnInvertChanged(bool value) => UpdateLutAndRender();

    /// <summary>Center와 Width를 함께 바꾼다. 하나씩 바꾸면 LUT와 렌더링이 두 번 일어나므로 묶어서 한 번만 처리한다.</summary>
    private void SetWindowLevel(double center, double width)
    {
        _suspendLutUpdate = true;
        try
        {
            WindowCenter = center;
            WindowWidth = width;
        }
        finally
        {
            _suspendLutUpdate = false;
        }

        UpdateLutAndRender();
    }

    private void UpdateLutAndRender()
    {
        if (_suspendLutUpdate)
        {
            return;
        }

        _lut = new WindowLevel(WindowCenter, WindowWidth).BuildLut(Invert);
        RenderCurrentFrame();
    }

    private void ApplyAutoWindowLevel(RawFrame frame)
    {
        (ushort min, ushort max) = PixelStatistics.MinMax(frame.Pixels);
        WindowLevel auto = WindowLevel.FromRange(min, max);
        SetWindowLevel(auto.Center, auto.Width);
    }

    // ── 수신 ─────────────────────────────────────────────

    private void StartReceiver()
    {
        var receiver = new FrameReceiver(Host, Port);
        receiver.FrameReceived += OnFrameReceived;
        receiver.StateChanged += state => _dispatcher.InvokeAsync(() => ConnectionState = state);
        receiver.EventRaised += e => _dispatcher.InvokeAsync(() => AddLog(LogEntry.From(e)));

        _receiver = receiver;
        _autoWindowLevelPending = true;
        _lastFramesReceived = 0;
        IsRunning = true;
        receiver.Start();
    }

    private async Task StopReceiverAsync()
    {
        FrameReceiver? receiver = _receiver;
        if (receiver is null)
        {
            return;
        }

        _receiver = null;
        await receiver.StopAsync().ConfigureAwait(true);
        receiver.FrameReceived -= OnFrameReceived;
        IsRunning = false;
        ConnectionState = ConnectionState.Stopped;
    }

    /// <summary>수신 스레드에서 호출된다. 최신 프레임만 보관하고, UI 스레드에 그리기를 한 번만 예약한다.</summary>
    private void OnFrameReceived(RawFrame frame)
    {
        Interlocked.Exchange(ref _pendingFrame, frame);
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
        {
            _dispatcher.InvokeAsync(RenderPendingFrame, DispatcherPriority.Render);
        }
    }

    private void RenderPendingFrame()
    {
        // 플래그를 먼저 내려야, 지금부터 들어오는 프레임이 다음 그리기를 예약할 수 있다.
        Volatile.Write(ref _renderQueued, 0);
        RawFrame? frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame is null)
        {
            return;
        }

        bool formatChanged = _currentFrame is null
            || _currentFrame.Width != frame.Width
            || _currentFrame.Height != frame.Height
            || _currentFrame.BitsStored != frame.BitsStored;
        _currentFrame = frame;

        if (formatChanged)
        {
            MaxPixelValue = frame.MaxValue;
        }

        if (_autoWindowLevelPending || formatChanged)
        {
            _autoWindowLevelPending = false;
            ApplyAutoWindowLevel(frame); // 내부에서 그린다
        }
        else
        {
            RenderCurrentFrame();
        }

        _framesDisplayed++;
        FrameInfo = $"#{frame.FrameId}  {frame.Width}×{frame.Height}  {frame.BitsStored}-bit";
    }

    private void RenderCurrentFrame()
    {
        if (_currentFrame is null)
        {
            return;
        }

        if (_renderer.Render(_currentFrame, _lut))
        {
            Image = _renderer.Bitmap;
        }

        RenderMs = _renderer.LastRenderMs;
    }

    private void RefreshStatistics()
    {
        DateTime now = DateTime.UtcNow;
        double seconds = (now - _lastStatisticsTime).TotalSeconds;
        _lastStatisticsTime = now;

        ReceiverStatistics? stats = _receiver?.GetStatistics();
        Statistics = stats;
        if (stats is not null && seconds > 0)
        {
            ReceiveFps = (stats.FramesReceived - _lastFramesReceived) / seconds;
            _lastFramesReceived = stats.FramesReceived;
        }
        else
        {
            ReceiveFps = 0;
        }

        DisplayFps = seconds > 0 ? (_framesDisplayed - _lastFramesDisplayed) / seconds : 0;
        _lastFramesDisplayed = _framesDisplayed;
    }

    private void AddLog(LogEntry entry)
    {
        Log.Insert(0, entry);
        if (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }
}
