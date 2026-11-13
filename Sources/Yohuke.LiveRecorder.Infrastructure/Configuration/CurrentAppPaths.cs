namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>向持久服务提供当前已发布实例的路径。</summary>
public sealed class CurrentAppPaths(CurrentInstanceStorage storage) : IAppPaths
{
    public string DataDirectory => storage.Current.Paths.DataDirectory;
    public string RecordsDirectory => storage.Current.Paths.RecordsDirectory;
    public string KeysDirectory => storage.Current.Paths.KeysDirectory;
    public string CacheDirectory => storage.Current.Paths.CacheDirectory;
    public string BootstrapFile => storage.Current.Paths.BootstrapFile;
    public string InstallationTokenFile => storage.Current.Paths.InstallationTokenFile;
}
