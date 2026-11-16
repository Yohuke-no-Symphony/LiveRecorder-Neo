namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Supplies an authorized capture identity without exposing account management operations.</summary>
public sealed record RecordingAccount
{
    public string AccountId { get; init; } = "";
    public long Uid { get; init; }
    public string Cookie { get; init; } = "";
}
