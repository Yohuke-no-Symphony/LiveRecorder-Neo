using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Runs external media processes with shared, independent concurrency limits.</summary>
public sealed class MediaProcessRunner : IDisposable
{
    private const int MAX_DIAGNOSTIC_LENGTH = 65536;
    private readonly MediaToolOptions options;
    private readonly ISettingsStore settings;
    private readonly DynamicProcessGate exports = new();
    private readonly DynamicProcessGate playbacks = new();
    private readonly DynamicProcessGate probes = new();

    /// <summary>Creates the shared process runner.</summary>
    public MediaProcessRunner(IOptions<MediaToolOptions> options, ISettingsStore settings)
    {
        this.options = options.Value;
        this.settings = settings;
    }

    internal async Task<MediaProcessResult> RunAsync(
        MediaProcessKind kind,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task>? input = null,
        IProgress<double>? progress = null,
        TimeSpan? expectedDuration = null)
    {
        var gate = kind switch
        {
            MediaProcessKind.Export => exports,
            MediaProcessKind.Playback => playbacks,
            _ => probes
        };
        var currentSettings = await settings.GetAsync(cancellationToken);
        var limit = kind switch
        {
            MediaProcessKind.Export => currentSettings.MediaConcurrency,
            MediaProcessKind.Playback => currentSettings.PlaybackConcurrency,
            _ => options.ProbeConcurrency
        };
        using var lease = await gate.AcquireAsync(limit, cancellationToken);
        var tool = kind == MediaProcessKind.Probe ? currentSettings.FfprobePath : currentSettings.FfmpegPath;
        return await RunProcessAsync(tool, arguments, cancellationToken, input, progress, expectedDuration);
    }

    internal async Task<MediaProcessReservation?> TryAcquirePlaybackPrefetchAsync(CancellationToken cancellationToken)
    {
        var currentSettings = await settings.GetAsync(cancellationToken);
        var lease = playbacks.TryAcquirePrefetch(currentSettings.PlaybackConcurrency, cancellationToken);
        return lease is null ? null : new(currentSettings.FfmpegPath, lease);
    }

    internal static async Task<MediaProcessResult> RunProcessAsync(
        string tool,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task>? input = null,
        IProgress<double>? progress = null,
        TimeSpan? expectedDuration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var cancellation = cancellationToken.Register(() => Kill(process));
        var output = new StringBuilder();
        var diagnostic = new StringBuilder();
        var outputTask = ReadOutputAsync(process.StandardOutput, output, progress, expectedDuration, cancellationToken);
        var errorTask = ReadDiagnosticAsync(process.StandardError, diagnostic, cancellationToken);
        var inputTask = input is null
            ? Task.CompletedTask
            : WriteInputAsync(process, input, cancellationToken);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), inputTask, outputTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new MediaToolException(tool, process.ExitCode, diagnostic.ToString());
            }
            progress?.Report(1);
            return new(output.ToString(), diagnostic.ToString());
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, Func<Stream, CancellationToken, Task> input, CancellationToken cancellationToken)
    {
        using var stream = new MediaProcessInputStream(process.StandardInput.BaseStream);
        try
        {
            await input(stream, cancellationToken);
        }
        catch (Exception exception) when (ReferenceEquals(exception, stream.WriteFailure))
        {
        }
        catch
        {
            Kill(process);
            throw;
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task ReadOutputAsync(StreamReader reader, StringBuilder output, IProgress<double>? progress, TimeSpan? expectedDuration, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            if (output.Length > MAX_DIAGNOSTIC_LENGTH)
            {
                output.Remove(0, output.Length - MAX_DIAGNOSTIC_LENGTH);
            }
            if (expectedDuration is { TotalMilliseconds: > 0 } duration
                && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
            {
                progress?.Report(Math.Clamp(microseconds / (duration.TotalMilliseconds * 1000), 0, 0.999));
            }
        }
    }

    private static async Task ReadDiagnosticAsync(StreamReader reader, StringBuilder diagnostic, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            diagnostic.AppendLine(line);
            if (diagnostic.Length > MAX_DIAGNOSTIC_LENGTH)
            {
                diagnostic.Remove(0, diagnostic.Length - MAX_DIAGNOSTIC_LENGTH);
            }
        }
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

    /// <summary>Releases concurrency resources after all hosted media work has stopped.</summary>
    public void Dispose()
    {
        exports.Dispose();
        playbacks.Dispose();
        probes.Dispose();
    }
}
