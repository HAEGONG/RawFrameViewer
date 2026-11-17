using System.Globalization;
using System.Windows.Data;

namespace RawFrameViewer.Viewer.Converters;

/// <summary>true ↔ false. 연결 중에는 호스트·포트 입력을 막는 데 쓴다.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
