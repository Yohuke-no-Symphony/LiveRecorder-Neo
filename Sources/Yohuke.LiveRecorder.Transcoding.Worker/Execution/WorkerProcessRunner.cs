using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Execution;

internal sealed class WorkerProcessRunner
{
    private const int MAX_OUTPUT_LENGTH = 262144;

    internal async Task<WorkerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, Action<double>? progress = null, double durationSeconds = 0,
        Action<string>? outputLine = null, IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? errorLine = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                start.Environment[variable.Key] = variable.Value;
            }
        }
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = cancellationToken.Register(() => Kill(process));
        var output = ReadAsync(process.StandardOutput, progress, durationSeconds, outputLine);
        var diagnostic = ReadAsync(process.StandardError, null, 0, errorLine);
        _ = output.ContinueWith(_ => Kill(process), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = diagnostic.ContinueWith(_ => Kill(process), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), output, diagnostic);
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, await output, await diagnostic);
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, diagnostic);
            throw;
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<double>? progress, double durationSeconds, Action<string>? outputLine)
    {
        var result = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            outputLine?.Invoke(line);
            result.AppendLine(line);
            if (result.Length > MAX_OUTPUT_LENGTH)
            {
                result.Remove(0, result.Length - MAX_OUTPUT_LENGTH);
            }
            if (progress is not null && durationSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var micros))
            {
                progress(Math.Clamp(micros / (durationSeconds * 1000000), 0, 0.999));
            }
        }
        return result.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
