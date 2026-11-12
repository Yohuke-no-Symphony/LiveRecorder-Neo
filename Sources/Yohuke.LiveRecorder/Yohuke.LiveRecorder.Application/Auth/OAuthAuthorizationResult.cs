namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>浏览器导航到提供方的授权地址。</summary>
public sealed record OAuthAuthorizationResult(string AuthorizationUrl, DateTime ExpiresAtUtc);
