using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawFrameViewer.Core.Imaging;
using RawFrameViewer.Viewer.ViewModels;

namespace RawFrameViewer.Viewer;

/// <summary>
/// 메인 창. 확대·이동·도구 선택·측정 도형처럼 화면에만 해당하는 상태는 여기서 처리하고,
/// 영상·W/L·연결·측정 결과 같은 데이터 상태는 MainViewModel에 맡긴다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "창이 닫힐 때 OnClosing에서 ViewModel을 정리한다.")]
public partial class MainWindow : Window
{
    private const double MinScale = 0.05;
    private const double MaxScale = 32;
    private const double WheelZoomFactor = 1.25;

    /// <summary>측정 도형 선 두께(화면 픽셀). 확대해도 같은 두께로 보이게 배율로 나눠 쓴다.</summary>
    private const double OverlayStrokeThickness = 2;

    private readonly MainViewModel _viewModel;

    private Tool _tool = Tool.Pan;
    private DragMode _dragMode;
    private Point _dragStart;          // 화면(Viewport) 좌표
    private Point _dragStartImage;     // 영상 픽셀 좌표
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

        _viewModel.MeasurementsCleared += () =>
        {
            MeasureLine.Visibility = Visibility.Collapsed;
            RoiRectangle.Visibility = Visibility.Collapsed;
        };

        // 데모·검증용: --connect 인자로 실행하면 바로 접속한다.
        Loaded += (_, _) =>
        {
            SetTransform(1, 0, 0);
            if (Environment.GetCommandLineArgs().Contains("--connect", StringComparer.OrdinalIgnoreCase))
            {
                _viewModel.ToggleConnectionCommand.Execute(null);
            }
        };
    }

    private enum Tool
    {
        Pan,
        Measure,
        Roi,
    }

    private enum DragMode
    {
        None,
        Pan,
        WindowLevel,
        Measure,
        Roi,
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

        // 도형은 영상과 함께 확대되므로, 선 두께를 배율로 나눠 화면에서는 늘 같은 두께로 보이게 한다.
        MeasureLine.StrokeThickness = OverlayStrokeThickness / scale;
        RoiRectangle.StrokeThickness = OverlayStrokeThickness / scale;

        ZoomText.Text = $"확대 {scale * 100:F0}%";
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

    // ── 마우스 ───────────────────────────────────────────

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(Viewport), e.Delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor);
        e.Handled = true;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitToWindow();
            return;
        }

        DragMode mode = e.ChangedButton switch
        {
            MouseButton.Right => DragMode.WindowLevel,
            MouseButton.Middle => DragMode.Pan,
            MouseButton.Left => _tool switch
            {
                Tool.Measure => DragMode.Measure,
                Tool.Roi => DragMode.Roi,
                _ => DragMode.Pan,
            },
            _ => DragMode.None,
        };

        if (mode == DragMode.None || (FrameImage.Source is null && mode is DragMode.Measure or DragMode.Roi))
        {
            return;
        }

        _dragMode = mode;
        _dragStart = e.GetPosition(Viewport);
        _dragStartImage = e.GetPosition(ImageLayer);
        _dragStartTranslate = new Point(ImageTranslate.X, ImageTranslate.Y);
        _dragStartCenter = _viewModel.WindowCenter;
        _dragStartWidth = _viewModel.WindowWidth;
        Viewport.CaptureMouse();
        e.Handled = true;

        if (mode == DragMode.Measure)
        {
            UpdateMeasure(_dragStartImage);
            MeasureLine.Visibility = Visibility.Visible;
        }
        else if (mode == DragMode.Roi)
        {
            UpdateRoi(_dragStartImage);
            RoiRectangle.Visibility = Visibility.Visible;
        }
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(Viewport);
        Point imagePosition = e.GetPosition(ImageLayer); // 영상 픽셀 좌표 (변환이 자동으로 반영됨)
        Vector delta = position - _dragStart;

        switch (_dragMode)
        {
            case DragMode.Pan:
                SetTransform(Scale, _dragStartTranslate.X + delta.X, _dragStartTranslate.Y + delta.Y);
                break;
            case DragMode.WindowLevel:
                _viewModel.AdjustWindowLevel(_dragStartCenter, _dragStartWidth, delta.X, delta.Y);
                break;
            case DragMode.Measure:
                UpdateMeasure(imagePosition);
                break;
            case DragMode.Roi:
                UpdateRoi(imagePosition);
                break;
        }

        _viewModel.UpdateCursor(imagePosition.X, imagePosition.Y);
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
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

    // ── 측정 도형 ───────────────────────────────────────

    private void UpdateMeasure(Point end)
    {
        MeasureLine.X1 = _dragStartImage.X;
        MeasureLine.Y1 = _dragStartImage.Y;
        MeasureLine.X2 = end.X;
        MeasureLine.Y2 = end.Y;
        _viewModel.SetMeasurement(_dragStartImage, end);
    }

    private void UpdateRoi(Point end)
    {
        // ROI는 픽셀 단위로 맞춘다. 그려진 사각형과 통계를 낸 영역이 정확히 같아야 하기 때문이다.
        PixelRect roi = PixelRect.FromCorners(
            (int)Math.Floor(_dragStartImage.X),
            (int)Math.Floor(_dragStartImage.Y),
            (int)Math.Floor(end.X),
            (int)Math.Floor(end.Y));

        Canvas.SetLeft(RoiRectangle, roi.X);
        Canvas.SetTop(RoiRectangle, roi.Y);
        RoiRectangle.Width = roi.Width;
        RoiRectangle.Height = roi.Height;
        _viewModel.SetRoi(roi);
    }

    // ── 기타 ─────────────────────────────────────────────

    private void ToolButton_Checked(object sender, RoutedEventArgs e)
    {
        _tool = sender == MeasureToolButton ? Tool.Measure
              : sender == RoiToolButton ? Tool.Roi
              : Tool.Pan;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 창 크기가 바뀌면 영상이 화면 밖으로 밀려나지 않도록 다시 맞춘다.
        FitToWindow();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e) => ShowActualSize();
}
