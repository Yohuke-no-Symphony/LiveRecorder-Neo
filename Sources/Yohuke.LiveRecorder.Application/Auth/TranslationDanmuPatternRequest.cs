namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前账号的同传正则，空值表示跟随全局默认。</summary>
public sealed record TranslationDanmuPatternRequest(string? Pattern);
