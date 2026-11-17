namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>保护缓存图片及其类型文件，直至读取或持久化复制结束。</summary>
public sealed class ArtworkCacheLease : IDisposable
{
    private Action? release;

    /// <summary>获取租约保护的图片路径。</summary>
    public string Path { get; }

    /// <summary>获取图片的媒体类型。</summary>
    public string ContentType { get; }

    internal ArtworkCacheLease(string path, string contentType, Action release)
    {
        Path = path;
        ContentType = contentType;
        this.release = release;
    }

    /// <summary>幂等释放当前读取租约，不影响其他使用者的租约。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
