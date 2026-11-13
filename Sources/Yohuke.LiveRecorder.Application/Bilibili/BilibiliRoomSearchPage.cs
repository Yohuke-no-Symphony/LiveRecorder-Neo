namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>一页直播间搜索结果及各来源的问题，不包含平台登录凭据。</summary>
public sealed record BilibiliRoomSearchPage(IReadOnlyList<BilibiliRoomSearchResult> Results, IReadOnlyList<BilibiliRoomSearchIssue> Issues, int Page, bool HasMore);
