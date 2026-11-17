namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Separates current network transfer progress from durably acknowledged recording commits.</summary>
public sealed record RecordingSessionTransferProgress
{
    public string ArtifactKind { get; init; } = "";
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
}
