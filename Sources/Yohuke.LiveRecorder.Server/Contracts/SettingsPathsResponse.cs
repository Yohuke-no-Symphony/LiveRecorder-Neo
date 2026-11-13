namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>当前生效目录、保存的目录配置及重启状态。</summary>
public sealed record SettingsPathsResponse(string DataDirectory, string RecordsDirectory, string CacheDirectory, string ConfigFile, string ConfiguredDataDirectory, string ConfiguredRecordsDirectory, bool RestartRequired);
