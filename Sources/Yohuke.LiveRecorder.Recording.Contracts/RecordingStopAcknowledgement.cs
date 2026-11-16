namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Releases durable room ownership only after capture stopped and all local sessions were uploaded.</summary>
public sealed record RecordingStopAcknowledgement
{
    public long PendingUploadBytes { get; init; }
    public List<string> SessionIds { get; init; } = [];
}
