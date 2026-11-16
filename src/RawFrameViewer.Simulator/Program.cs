using RawFrameViewer.Simulator;

Console.OutputEncoding = System.Text.Encoding.UTF8;

SimulatorOptions? options = SimulatorOptions.Parse(args, out string? error);
if (options is null)
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
await new DetectorServer(options).RunAsync(cts.Token);
Console.WriteLine("종료");
return 0;
