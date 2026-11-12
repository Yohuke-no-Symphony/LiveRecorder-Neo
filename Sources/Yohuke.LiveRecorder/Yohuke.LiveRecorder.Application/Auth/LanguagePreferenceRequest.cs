namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前用户的界面语言；空值表示跟随全局默认。</summary>
public sealed record LanguagePreferenceRequest(string? Language);
