namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>独立于工作目录的应用数据路径。</summary>
public sealed class AppPaths
{
    public string DataDirectory { get; }

    /// <summary>固定配置与密钥目录。</summary>
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Environment.GetEnvironmentVariable("LIVE_RECORDER_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"));
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
