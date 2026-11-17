namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal readonly record struct EventArchiveFileVersion(long Length, long CreatedUtcTicks, long ModifiedUtcTicks)
{
    internal static EventArchiveFileVersion Capture(FileStream stream) => new(
        RandomAccess.GetLength(stream.SafeFileHandle),
        File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks,
        File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks);
}
