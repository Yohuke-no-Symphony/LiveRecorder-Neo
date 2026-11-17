using Yohuke.LiveRecorder.Transcoding.Worker.Services;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

internal static class WorkerServiceHost
{
    internal static Task RunAsync(string configurationPath, CancellationToken cancellationToken) =>
        WorkerServiceRunner.RunAsync(configurationPath, WorkerServiceIdentity.Transcoding, WorkerApplication.RunAsync, cancellationToken);
}
