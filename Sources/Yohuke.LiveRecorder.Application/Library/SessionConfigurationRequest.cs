namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>在已读取的配置版本上更新录像共享配置；空覆盖表示跟随全局。</summary>
public sealed record SessionConfigurationRequest(string? TranslationDanmuPatternOverride, int ExpectedRevision);
