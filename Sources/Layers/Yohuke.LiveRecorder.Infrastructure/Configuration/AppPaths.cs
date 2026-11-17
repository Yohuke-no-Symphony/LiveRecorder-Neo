namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>由启动配置确定的实例数据、私有配置和媒体目录。</summary>
public sealed class AppPaths : IAppPaths
{
    public string DataDirectory { get; }
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public string RecordsDirectory { get; }
    public string BootstrapFile => Path.Combine(DataDirectory, "bootstrap.json");
    public string InstallationTokenFile => Path.Combine(DataDirectory, "installation-token");

    /// <summary>仅解析实例数据和录像目录，不创建目录或写入文件。</summary>
    public AppPaths(string dataDirectory, string recordsDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        RecordsDirectory = Path.GetFullPath(recordsDirectory);
    }

    /// <summary>在安装提交或已安装实例启动修复时创建所需目录。</summary>
    public void EnsureDirectories()
    {
        EnsureDataDirectory(DataDirectory);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RecordsDirectory);
    }

    internal static void EnsureDataDirectory(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
