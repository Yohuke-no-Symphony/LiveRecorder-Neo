namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>公开分享列表的创建时间和标识分页位置。</summary>
public sealed record PublicClipShareCursor(DateTime BeforeUtc, string BeforeId);
