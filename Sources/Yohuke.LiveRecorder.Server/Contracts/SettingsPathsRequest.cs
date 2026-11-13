namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>保存到唯一设置文件、下次启动生效的两个存储目录。</summary>
public sealed record SettingsPathsRequest(string DataDirectory, string RecordsDirectory);
