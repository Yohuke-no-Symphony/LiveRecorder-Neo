namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed record RecordingSpoolSession(string DirectoryPath, RecordingSessionState State);
