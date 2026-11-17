using System.Globalization;
using RawFrameViewer.Core.Protocol;

namespace RawFrameViewer.Simulator;

/// <summary>명령줄 옵션</summary>
internal sealed record SimulatorOptions
{
    public int Port { get; init; } = 5000;
    public double Fps { get; init; } = 10;
    public ushort Width { get; init; } = 2048;
    public ushort Height { get; init; } = 2048;
    public byte BitsStored { get; init; } = 14;

    /// <summary>오류 주입 일정. 예: "F1@20,F2@40"</summary>
    public string? InjectSchedule { get; init; }

    public const string Usage = """
        사용법: RawFrameViewer.Simulator [옵션]
          --port <번호>     대기 포트 (기본 5000)
          --fps <값>        초당 프레임 수, 0이면 Heartbeat만 전송 (기본 10)
          --width <픽셀>    가로 1~4096 (기본 2048)
          --height <픽셀>   세로 1~4096 (기본 2048)
          --bits <비트>     BitsStored 8~16 (기본 14)
          --inject <일정>   오류 주입 일정, FrameId 기준. 연결마다 반복 (예: F1@20,F2@40,F3@60)

        실행 중 키보드로 다음 프레임에 오류 주입:
          1 프레임 누락   2 영상 손상   3 헤더 손상
          4 연결 끊기     5 전송 정지   6 범위 초과 픽셀
        """;

    /// <summary>명령줄을 해석한다. 잘못된 입력이면 오류 메시지와 함께 null을 반환한다.</summary>
    public static SimulatorOptions? Parse(string[] args, out string? error)
    {
        var options = new SimulatorOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (i + 1 >= args.Length)
            {
                error = $"{name} 뒤에 값이 없습니다.";
                return null;
            }

            string value = args[++i];
            switch (name)
            {
                case "--port" when int.TryParse(value, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535:
                    options = options with { Port = port };
                    break;
                case "--fps" when double.TryParse(value, CultureInfo.InvariantCulture, out double fps) && fps is >= 0 and <= 120:
                    options = options with { Fps = fps };
                    break;
                case "--width" when ushort.TryParse(value, CultureInfo.InvariantCulture, out ushort width) && width is > 0 and <= ProtocolConstants.MaxDimension:
                    options = options with { Width = width };
                    break;
                case "--height" when ushort.TryParse(value, CultureInfo.InvariantCulture, out ushort height) && height is > 0 and <= ProtocolConstants.MaxDimension:
                    options = options with { Height = height };
                    break;
                case "--bits" when byte.TryParse(value, CultureInfo.InvariantCulture, out byte bits) && bits is >= ProtocolConstants.MinBitsStored and <= ProtocolConstants.MaxBitsStored:
                    options = options with { BitsStored = bits };
                    break;
                case "--inject" when !string.IsNullOrWhiteSpace(value):
                    options = options with { InjectSchedule = value };
                    break;
                default:
                    error = $"알 수 없는 옵션이거나 값이 범위를 벗어났습니다: {name} {value}";
                    return null;
            }
        }

        return options;
    }
}
