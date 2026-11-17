namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>可添加的直播间公开资料，数字标识保持字符串以避免客户端精度损失。</summary>
public sealed record BilibiliRoomSearchResult(string RoomId, string ShortRoomId, string OwnerUid, string OwnerName, string Title, bool IsLive, IReadOnlyList<string> Sources);
