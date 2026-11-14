namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>归组时明确选择的统一配置；空正则明确选择跟随全局。</summary>
public sealed record SessionGroupConfiguration(string? TranslationDanmuPatternOverride);
