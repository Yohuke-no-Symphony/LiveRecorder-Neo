using System.Text.Json.Serialization;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>Retains each durable room assignment and its historical ingestion fence after ownership is released.</summary>
public sealed class RecordingAssignmentRecord : Entity
{
    public long RoomId { get; set; }
    public string TargetId { get; set; } = "";
    public string OwnerKind { get; set; } = "Worker";
    public string OwnerId { get; set; } = "";
    public int Generation { get; set; }
    public string State { get; set; } = "Owned";
    public int ConfigurationRevision { get; set; } = 1;
    public string ConfigurationFingerprint { get; set; } = "";
    [JsonIgnore]
    public string TokenHash { get; set; } = "";
    [JsonIgnore]
    public string ProtectedToken { get; set; } = "";
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? LastCapturedAtUtc { get; set; }
    public long PendingUploadBytes { get; set; }
    public long SpoolBytes { get; set; }
    public string? CurrentSessionId { get; set; }
    public string? Error { get; set; }
    [JsonIgnore]
    public RecordingAssignment? Definition { get; set; }
}
