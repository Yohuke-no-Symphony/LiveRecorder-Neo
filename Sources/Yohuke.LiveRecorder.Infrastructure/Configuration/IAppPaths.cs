namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>提供当前实例的私有数据目录和独立录像目录。</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string RecordsDirectory { get; }
    string KeysDirectory { get; }
    string CacheDirectory { get; }
    string BootstrapFile { get; }
    string InstallationTokenFile { get; }
}
