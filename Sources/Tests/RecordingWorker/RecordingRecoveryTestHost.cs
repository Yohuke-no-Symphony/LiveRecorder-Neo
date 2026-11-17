using System.Diagnostics;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Http.Tests;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal static class RecordingRecoveryTestHost
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "record")
        {
            using var settings = new WorkerSettingsStore(args[1], "Recording");
            await settings.LoadAsync(CancellationToken.None);
            using var media = new HttpClient(new RecordingLiveTestHttpHandler(await File.ReadAllBytesAsync(args[2]), TimeSpan.FromMilliseconds(20)));
            await new RecordingWorkerRuntime(settings.Current.Worker, settings, Console.WriteLine,
                () => new RecordingRecoverySource(), selected => new FlvMediaRecorder(media, Options.Create(new MediaToolOptions()),
                    new RecordingMediaSettingsStore(selected))).RunAsync(CancellationToken.None);
            return 0;
        }
        if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] is "job" or "job-exit")
        {
            WindowsWorkerProcessJob.AttachCurrentProcess();
            using var child = Process.Start(StartInfo("child", args[1]))!;
            while (!File.Exists(args[1] + ".grandchild"))
            {
                await Task.Delay(10);
            }
            if (args[0] == "job-exit")
            {
                return 0;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (args.Length == 2 && args[0] == "child")
        {
            await File.WriteAllTextAsync(args[1] + ".child", Environment.ProcessId.ToString());
            using var child = Process.Start(StartInfo("grandchild", args[1]))!;
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (args.Length == 2 && args[0] == "grandchild")
        {
            await File.WriteAllTextAsync(args[1] + ".grandchild", Environment.ProcessId.ToString());
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        return 2;
    }

    internal static ProcessStartInfo StartInfo(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(RecordingRecoveryTestHost).Assembly.Location);
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }
}
