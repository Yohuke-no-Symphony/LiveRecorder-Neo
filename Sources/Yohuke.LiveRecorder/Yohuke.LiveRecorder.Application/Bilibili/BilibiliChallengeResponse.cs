namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>用户完成极验或图片挑战后产生的证明。</summary>
public sealed record BilibiliChallengeResponse(string Token, string? Challenge = null, string? Validate = null, string? Seccode = null, string? LotNumber = null, string? CaptchaOutput = null, string? PassToken = null, string? GenTime = null, string? Captcha = null);
