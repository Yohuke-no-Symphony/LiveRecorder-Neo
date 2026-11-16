namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Reports bounded room metadata without mixing capture freshness with control connection freshness.</summary>
public sealed record RecordingRoomSnapshot
{
    public long RoomId { get; init; }
    public long Uid { get; init; }
    public string Name { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public bool IsLive { get; init; }
    public DateTime CheckedAtUtc { get; init; }
}
