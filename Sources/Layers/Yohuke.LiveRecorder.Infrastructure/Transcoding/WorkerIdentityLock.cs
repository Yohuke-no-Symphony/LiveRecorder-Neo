using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

internal static class WorkerIdentityLock
{
    internal static Task<FileStream> AcquireAsync(IAppPaths paths, string workerId, CancellationToken cancellationToken) =>
        MediaJobExecutionLock.AcquireAsync(paths, "worker-" + TranscodeAssetRegistry.Hash(workerId), cancellationToken);
}
