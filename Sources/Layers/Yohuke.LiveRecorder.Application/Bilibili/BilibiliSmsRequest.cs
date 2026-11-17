namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>创建手机号短信登录流程。</summary>
public sealed record BilibiliSmsRequest(string Cid, string Tel);
