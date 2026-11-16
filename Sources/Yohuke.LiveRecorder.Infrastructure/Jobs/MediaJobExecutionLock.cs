using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

/// <summary>Serializes job claiming, publication, revocation and cleanup across server processes.</summary>
public static class MediaJobExecutionLock
{
    /// <summary>Acquires a persistent lock file outside directories removed by attempt cleanup.</summary>
    public static async Task<FileStream> AcquireAsync(IAppPaths paths, string jobId, CancellationToken cancellationToken = default)
    {
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "job-locks"));
        Directory.CreateDirectory(directory);
        var path = MediaPaths.Resolve(directory, jobId + ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
