using System.Globalization;
using Bench;
using Bench.Cli;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var log = new ConsoleLog();

try
{
    return await Commands.RunAsync(args, log, cts.Token);
}
catch (BenchException ex)
{
    log.Done();
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (ToolNotFoundException ex)
{
    log.Done();
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    log.Done();
    Console.Error.WriteLine("cancelled");
    return 130;
}
