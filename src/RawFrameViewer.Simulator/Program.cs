using RawFrameViewer.Simulator;

Console.OutputEncoding = System.Text.Encoding.UTF8;

SimulatorOptions? options = SimulatorOptions.Parse(args, out string? error);
FaultInjector? faults = FaultInjector.None;
if (options?.InjectSchedule is { } schedule)
{
    faults = FaultInjector.Parse(schedule, out error);
}

if (options is null || faults is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(SimulatorOptions.Usage);
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C로 정상 종료
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("RawFrameViewer 가상 검출기 시뮬레이터 (임상용 아님). 종료: Ctrl+C");
if (options.InjectSchedule is not null)
{
    Console.WriteLine($"오류 주입 일정: {options.InjectSchedule} (연결마다 반복)");
}

// 키보드로 오류 주입. 입력이 리디렉션된 경우(자동 실행)에는 쓰지 않는다.
if (!Console.IsInputRedirected)
{
    Console.WriteLine("오류 주입 키: 1 누락 · 2 영상 손상 · 3 헤더 손상 · 4 연결 끊기 · 5 전송 정지 · 6 범위 초과");
    _ = Task.Factory.StartNew(
        () =>
        {
            while (!cts.IsCancellationRequested)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.KeyChar is >= '1' and <= '6')
                {
                    var kind = (FaultKind)(key.KeyChar - '0');
                    faults.Request(kind);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 요청: {FaultInjector.Describe(kind)} → 다음 프레임에 적용");
                }
            }
        },
        cts.Token,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);
}

await new DetectorServer(options, faults).RunAsync(cts.Token);
Console.WriteLine("종료");
return 0;
