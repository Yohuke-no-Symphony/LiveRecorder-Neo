using System.Diagnostics;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal sealed class CpuUsageMonitor
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CpuLoadWindow window = new();

    internal CpuLoadSnapshot Snapshot(int windowSeconds) => window.Read(clock.Elapsed, windowSeconds);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                var counters = OperatingSystem.IsWindows() ? WindowsCpuReader.Read()
                    : OperatingSystem.IsMacOS() ? MacCpuReader.Read()
                    : OperatingSystem.IsLinux() ? LinuxCpuReader.Read()
                    : throw new PlatformNotSupportedException("CPU load sampling is unavailable on this operating system");
                window.Add(clock.Elapsed, counters);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                             or FormatException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                window.Add(clock.Elapsed, null, exception.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
