namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>由用户通过平台验证码控件完成的挑战。</summary>
public sealed record BilibiliChallenge(string Type, string Token, string Gt, string? Challenge, string? ImageUrl);
