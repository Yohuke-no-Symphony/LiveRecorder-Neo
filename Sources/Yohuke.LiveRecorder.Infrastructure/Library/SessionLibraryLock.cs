using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

internal static class SessionLibraryLock
{
    internal static async Task<FileStream> AcquireAsync(string recordsRoot, CancellationToken cancellationToken)
    {
        var path = MediaPaths.Resolve(recordsRoot, Path.Combine(".work", "library.lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (IOException)
            {
                throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
            }
        }
    }
}
