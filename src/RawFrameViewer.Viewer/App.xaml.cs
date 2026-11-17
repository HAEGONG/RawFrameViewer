using System.Windows;
using System.Windows.Threading;

namespace RawFrameViewer.Viewer;

/// <summary>
/// 애플리케이션 진입점. 처리되지 않은 예외로 프로그램이 아무 말 없이 꺼지지 않도록,
/// 마지막 방어선으로 오류를 사용자에게 보여 준다.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"예상하지 못한 오류가 발생했습니다.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
            "RawFrameViewer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // 화면 하나의 동작에서 난 오류로 영상 수신 전체를 멈추지 않는다.
        e.Handled = true;
    }
}
