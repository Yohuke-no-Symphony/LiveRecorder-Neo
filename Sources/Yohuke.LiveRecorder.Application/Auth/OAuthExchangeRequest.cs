namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>浏览器收到的授权码及防伪 state。</summary>
public sealed record OAuthExchangeRequest(string State, string Code);
