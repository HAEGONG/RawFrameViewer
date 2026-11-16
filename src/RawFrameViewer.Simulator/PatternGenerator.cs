namespace RawFrameViewer.Simulator;

/// <summary>
/// 시험용 16-bit 패턴을 만든다. 실제 X-ray 영상을 흉내 내지 않으며, 뷰어 기능을 눈으로 확인하기 위한 패턴이다.
/// - 대각선 그라데이션 배경
/// - 밝기가 단계적으로 변하는 띠(Step wedge): Window/Level 조절 효과 확인용
/// - 큰 원: 측정·ROI 확인용
/// - 움직이는 작은 원: 프레임이 실제로 갱신되는지 확인용
/// - 약한 노이즈: 같은 프레임이 반복되지 않게 함
/// </summary>
internal sealed class PatternGenerator
{
    private const int StepCount = 8;

    private readonly int _width;
    private readonly int _height;
    private readonly ushort _max;
    private readonly ushort[] _background;
    private uint _noiseState = 0x9E3779B9;

    public PatternGenerator(int width, int height, byte bitsStored)
    {
        _width = width;
        _height = height;
        _max = (ushort)((1 << bitsStored) - 1);
        _background = BuildBackground();
    }

    /// <summary>프레임 하나를 만든다. frameIndex에 따라 움직이는 원의 위치가 바뀐다.</summary>
    public ushort[] Next(uint frameIndex)
    {
        ushort[] pixels = (ushort[])_background.Clone();
        DrawMovingDisc(pixels, frameIndex);
        AddNoise(pixels);
        return pixels;
    }

    private ushort Level(double ratio) => (ushort)Math.Clamp(Math.Round(ratio * _max), 0, _max);

    private ushort[] BuildBackground()
    {
        var pixels = new ushort[_width * _height];

        // 대각선 그라데이션: 최댓값의 12%~37%
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                double t = (x + y) / (double)(_width + _height);
                pixels[(y * _width) + x] = Level(0.12 + (0.25 * t));
            }
        }

        // Step wedge: 아래쪽 띠를 8칸으로 나눠 18%~92% 밝기
        int bandTop = (int)(_height * 0.80);
        int bandBottom = (int)(_height * 0.92);
        for (int y = bandTop; y < bandBottom; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                int step = Math.Min(x * StepCount / _width, StepCount - 1);
                pixels[(y * _width) + x] = Level(0.18 + (0.74 * step / (StepCount - 1)));
            }
        }

        // 큰 원: 중앙 위쪽, 밝기 55%
        FillDisc(pixels, _width * 0.5, _height * 0.38, Math.Min(_width, _height) * 0.22, Level(0.55));
        return pixels;
    }

    private void DrawMovingDisc(ushort[] pixels, uint frameIndex)
    {
        // 약 6초(60프레임 @10fps)에 한 바퀴 도는 작은 밝은 원
        double angle = frameIndex * (2 * Math.PI / 60);
        double radius = Math.Min(_width, _height) * 0.30;
        double cx = (_width * 0.5) + (radius * Math.Cos(angle));
        double cy = (_height * 0.45) + (radius * Math.Sin(angle));
        FillDisc(pixels, cx, cy, Math.Min(_width, _height) * 0.04, Level(0.85));
    }

    private void FillDisc(ushort[] pixels, double cx, double cy, double r, ushort value)
    {
        int x0 = Math.Max(0, (int)(cx - r));
        int x1 = Math.Min(_width - 1, (int)(cx + r));
        int y0 = Math.Max(0, (int)(cy - r));
        int y1 = Math.Min(_height - 1, (int)(cy + r));
        double r2 = r * r;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                if ((dx * dx) + (dy * dy) <= r2)
                {
                    pixels[(y * _width) + x] = value;
                }
            }
        }
    }

    /// <summary>
    /// ±1% 정도의 노이즈를 더한다. 픽셀이 수백만 개라 System.Random 대신 가벼운 xorshift를 쓴다.
    /// 결과는 항상 0~_max 범위로 자른다.
    /// </summary>
    private void AddNoise(ushort[] pixels)
    {
        int amplitude = Math.Max(1, _max / 100);
        uint state = _noiseState;
        for (int i = 0; i < pixels.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            int noise = (int)(state % (uint)((2 * amplitude) + 1)) - amplitude;
            pixels[i] = (ushort)Math.Clamp(pixels[i] + noise, 0, _max);
        }

        _noiseState = state;
    }
}
