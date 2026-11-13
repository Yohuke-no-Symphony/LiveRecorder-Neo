namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>可与成功结果共同返回的查找问题，包含本地化标识或已脱敏的远端说明。</summary>
public sealed record BilibiliRoomSearchIssue(IReadOnlyList<string> Sources, int? ApiCode, string? Code, string Message);
