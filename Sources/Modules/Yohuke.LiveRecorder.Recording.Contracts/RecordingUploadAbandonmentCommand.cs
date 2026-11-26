namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Requests local spool cleanup for one closed recording capture.</summary>
public sealed record RecordingUploadAbandonmentCommand
{
    public string SessionId { get; init; } = "";
    public string AssignmentId { get; init; } = "";
    public int Generation { get; init; }
    public string Token { get; init; } = "";
}
