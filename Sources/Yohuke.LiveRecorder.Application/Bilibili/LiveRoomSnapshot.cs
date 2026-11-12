namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>一次查询得到的主播和直播间快照。</summary>
public sealed record LiveRoomSnapshot(long RoomId, long Uid, string Name, string AvatarUrl, string Title, string CoverUrl, bool IsLive, DateTime? LiveStartedAtUtc);
