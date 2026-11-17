namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Reports one locally durable broadcast independently of room ownership and library publication.</summary>
public sealed record RecordingSessionProgress
{
    public const int MAX_BATCH_COUNT = 128;

    public string AssignmentId { get; init; } = "";
    public int Generation { get; init; }
    public string Token { get; init; } = "";
    public RecordingSessionDescriptor Descriptor { get; init; } = new();
    public string CaptureState { get; init; } = "Paused";
    public double CapturedDurationMs { get; init; }
    public long CommittedArchiveBytes { get; init; }
    public long AcknowledgedArchiveBytes { get; init; }
    public DateTime? LastCapturedAtUtc { get; init; }
    public string UploadState { get; init; } = "Queued";
    public RecordingSessionTransferProgress? CurrentTransfer { get; init; }
    public string? ErrorCode { get; init; }
}
