using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

internal static class MediaJobFiles
{
    internal static string OutputDirectory(MediaJob job, BroadcastSession session, string root, int attempt)
    {
        var sessionDirectory = MediaPaths.Resolve(root, session.DirectoryPath);
        var kind = job.Kind == "Clip" ? Path.Combine("切片", job.OwnerId, job.Id) : Path.Combine(job.Kind == "Repair" ? "修复" : "合并", job.Id);
        return MediaPaths.Resolve(sessionDirectory, Path.Combine(kind, "v" + attempt));
    }

    internal static async Task<FileStream> AcquirePublicationLockAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".media-publication.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
