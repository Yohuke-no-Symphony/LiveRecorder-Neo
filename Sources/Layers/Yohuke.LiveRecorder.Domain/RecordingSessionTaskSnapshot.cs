using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>Retains the last authenticated per-broadcast worker observation without creating library media.</summary>
public sealed class RecordingSessionTaskSnapshot : Entity
{
    public string WorkerId { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public int Generation { get; set; }
    public string TargetId { get; set; } = "";
    public RecordingSessionDescriptor Descriptor { get; set; } = new();
    public string CaptureState { get; set; } = "Paused";
    public double CapturedDurationMs { get; set; }
    public long CommittedArchiveBytes { get; set; }
    public long AcknowledgedArchiveBytes { get; set; }
    public DateTime? LastCapturedAtUtc { get; set; }
    public string UploadState { get; set; } = "Queued";
    public RecordingSessionTransferProgress? CurrentTransfer { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime LastReportedAtUtc { get; set; }
}
