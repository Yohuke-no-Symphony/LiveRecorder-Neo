using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed record RecordingSessionUploadActivity(string State, RecordingSessionTransferProgress? Transfer = null, string? ErrorCode = null);
