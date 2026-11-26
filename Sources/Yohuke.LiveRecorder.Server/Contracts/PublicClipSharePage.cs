namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>一页公开分享及继续读取的位置。</summary>
public sealed record PublicClipSharePage(IReadOnlyList<PublicClipShareEntry> Items, PublicClipShareCursor? NextCursor);
