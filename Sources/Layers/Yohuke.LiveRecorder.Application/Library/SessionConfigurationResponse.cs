namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>录像共享配置及当前生效的同传规则。</summary>
public sealed record SessionConfigurationResponse(string? TranslationDanmuPatternOverride, string EffectiveTranslationDanmuPattern, int Revision);
