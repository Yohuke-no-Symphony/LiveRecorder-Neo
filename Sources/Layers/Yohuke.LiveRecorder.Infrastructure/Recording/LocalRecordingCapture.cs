namespace Yohuke.LiveRecorder.Infrastructure.Recording;

internal sealed record LocalRecordingCapture(string Id, string DirectoryPath, string RelativePathPrefix, DateTime StartedAtUtc);
