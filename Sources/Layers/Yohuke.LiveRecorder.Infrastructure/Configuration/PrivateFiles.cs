using System.Text;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>在同一目录内以私有权限原子替换实例配置。</summary>
public static class PrivateFiles
{
    /// <summary>先写入独立临时文件，成功后替换目标，失败时保留原配置。</summary>
    public static async Task WriteAllTextAsync(string path, string value, CancellationToken cancellationToken = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(value), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
