namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>切片所有者或管理员可读取的分享链接状态。</summary>
public sealed record ClipShareLinkResponse(string? Token, string? Visibility = null);
