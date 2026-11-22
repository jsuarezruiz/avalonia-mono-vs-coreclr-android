using System.Diagnostics;
using System.Text;

namespace Bench;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs external tools (adb, ffmpeg) with an argument list, never through a shell.</summary>
public static class ProcessRunner
{
    public static async Task<ProcResult> RunAsync(
        string file, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var process = Create(file, args, redirectStdin: false, redirectStdout: true);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolNotFoundException(file, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{file} {string.Join(' ', args)} did not finish within {timeout}");
        }

        // The async readers finish after the exit event; this call waits for them to drain.
        process.WaitForExit();
        return new ProcResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Starts a process and leaves it running, for streaming stdin or stdout.</summary>
    public static Process Start(string file, IEnumerable<string> args, bool redirectStdin = false, bool redirectStdout = false,
        Action<string>? onError = null)
    {
        var process = Create(file, args, redirectStdin, redirectStdout);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new ToolNotFoundException(file, ex);
        }

        // Nobody reads stderr of a long-running tool; drain it so the child can never block on a full pipe.
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onError?.Invoke(e.Data); };
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Runs a long command and hands each output line (stdout and stderr) to <paramref name="onLine"/> as it appears.
    /// Returns the exit code. <paramref name="environment"/> entries are set (or removed, for null) for the child only.
    /// </summary>
    public static async Task<int> RunStreamingAsync(
        string file, IEnumerable<string> args, string? workingDirectory, IReadOnlyDictionary<string, string?>? environment,
        Action<string> onLine, CancellationToken ct = default)
    {
        using var process = Create(file, args, redirectStdin: false, redirectStdout: true, workingDirectory, environment);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new ToolNotFoundException(file, ex); }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static Process Create(string file, IEnumerable<string> args, bool redirectStdin, bool redirectStdout,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
            }
        }

        foreach (var a in args) info.ArgumentList.Add(a);
        return new Process { StartInfo = info };
    }
}

public sealed class ToolNotFoundException(string tool, Exception inner)
    : Exception($"'{tool}' was not found. Install it and make sure it is on PATH (or set the matching environment variable).", inner);
