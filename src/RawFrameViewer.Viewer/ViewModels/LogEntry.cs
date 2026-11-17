using RawFrameViewer.Core.Networking;

namespace RawFrameViewer.Viewer.ViewModels;

/// <summary>화면 이벤트 로그 한 줄</summary>
public sealed record LogEntry(string Time, ReceiverEventLevel Level, string Kind, string Message)
{
    public static LogEntry From(ReceiverEvent e) =>
        new(e.Time.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), e.Level, e.Kind.ToString(), e.Message);
}
