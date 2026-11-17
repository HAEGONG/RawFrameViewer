using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawFrameViewer.Viewer.ViewModels;

namespace RawFrameViewer.Viewer;

/// <summary>
/// 메인 창. 확대·이동처럼 화면에만 해당하는 상태는 여기서 처리하고,
/// 영상·W/L·연결 같은 데이터 상태는 MainViewModel에 맡긴다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "창이 닫힐 때 OnClosing에서 ViewModel을 정리한다.")]
public partial class MainWindow : Window
{
    private const double MinScale = 0.05;
    private const double MaxScale = 32;
    private const double WheelZoomFactor = 1.25;

    private readonly MainViewModel _viewModel;

    private DragMode _dragMode;
    private Point _dragStart;
    private Point _dragStartTranslate;
    private double _dragStartCenter;
    private double _dragStartWidth;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        // 새 비트맵(첫 프레임이거나 영상 크기 변경)이 오면 화면에 맞춘다.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Image))
            {
                Dispatcher.InvokeAsync(FitToWindow, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };

        // 데모·검증용: --connect 인자로 실행하면 바로 접속한다.
        Loaded += (_, _) =>
        {
            UpdateZoomText();
            if (Environment.GetCommandLineArgs().Contains("--connect", StringComparer.OrdinalIgnoreCase))
            {
                _viewModel.ToggleConnectionCommand.Execute(null);
            }
        };
    }

    private enum DragMode
    {
        None,
        Pan,
        WindowLevel,
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        await _viewModel.DisposeAsync().ConfigureAwait(true);
    }

    // ── 확대·이동 ───────────────────────────────────────

    private double Scale => ImageScale.ScaleX;

    private void SetTransform(double scale, double translateX, double translateY)
    {
        scale = Math.Clamp(scale, MinScale, MaxScale);
        ImageScale.ScaleX = scale;
        ImageScale.ScaleY = scale;
        ImageTranslate.X = translateX;
        ImageTranslate.Y = translateY;

        // 확대했을 때는 픽셀 경계를 그대로 보여주고(보간 없음), 축소했을 때는 부드럽게 줄인다.
        RenderOptions.SetBitmapScalingMode(FrameImage, scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        UpdateZoomText();
    }

    /// <summary>화면의 한 점(anchor)을 고정한 채로 확대·축소한다. 마우스 아래 지점이 그대로 남는다.</summary>
    private void ZoomAt(Point anchor, double factor)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        double ratio = newScale / oldScale;
        SetTransform(
            newScale,
            anchor.X - ((anchor.X - ImageTranslate.X) * ratio),
            anchor.Y - ((anchor.Y - ImageTranslate.Y) * ratio));
    }

    private void FitToWindow()
    {
        if (FrameImage.Source is not BitmapSource bitmap || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
        {
            return;
        }

        double scale = Math.Min(Viewport.ActualWidth / bitmap.PixelWidth, Viewport.ActualHeight / bitmap.PixelHeight);
        SetTransform(
            scale,
            (Viewport.ActualWidth - (bitmap.PixelWidth * scale)) / 2,
            (Viewport.ActualHeight - (bitmap.PixelHeight * scale)) / 2);
    }

    private void ShowActualSize()
    {
        if (FrameImage.Source is not BitmapSource bitmap)
        {
            return;
        }

        SetTransform(
            1,
            (Viewport.ActualWidth - bitmap.PixelWidth) / 2,
            (Viewport.ActualHeight - bitmap.PixelHeight) / 2);
    }

    private void UpdateZoomText() => ZoomText.Text = $"확대 {Scale * 100:F0}%";

    // ── 마우스 ───────────────────────────────────────────

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(Viewport), e.Delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor);
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FitToWindow();
            return;
        }

        BeginDrag(DragMode.Pan, e);
    }

    private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartCenter = _viewModel.WindowCenter;
        _dragStartWidth = _viewModel.WindowWidth;
        BeginDrag(DragMode.WindowLevel, e);
    }

    private void BeginDrag(DragMode mode, MouseButtonEventArgs e)
    {
        _dragMode = mode;
        _dragStart = e.GetPosition(Viewport);
        _dragStartTranslate = new Point(ImageTranslate.X, ImageTranslate.Y);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(Viewport);
        Vector delta = position - _dragStart;

        switch (_dragMode)
        {
            case DragMode.Pan:
                SetTransform(Scale, _dragStartTranslate.X + delta.X, _dragStartTranslate.Y + delta.Y);
                break;
            case DragMode.WindowLevel:
                _viewModel.AdjustWindowLevel(_dragStartCenter, _dragStartWidth, delta.X, delta.Y);
                break;
        }

        // 영상 기준 좌표. Stretch=None, 96 DPI 비트맵이므로 1 단위 = 1 픽셀이다.
        Point pixel = e.GetPosition(FrameImage);
        _viewModel.UpdateCursor(pixel.X, pixel.Y);
    }

    private void Viewport_MouseButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragMode = DragMode.None;
        Viewport.ReleaseMouseCapture();
    }

    private void Viewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            _viewModel.UpdateCursor(-1, -1);
        }
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 창 크기가 바뀌면 영상이 화면 밖으로 밀려나지 않도록 다시 맞춘다.
        FitToWindow();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e) => ShowActualSize();
}
