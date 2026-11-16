using Microsoft.AspNetCore.Http;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在图片响应执行结束或失败后释放缓存读取租约。</summary>
public sealed class ArtworkFileResult(ArtworkCacheLease image, bool lastModified = false) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try
        {
            await Results.File(image.Path, image.ContentType,
                lastModified: lastModified ? File.GetLastWriteTimeUtc(image.Path) : null).ExecuteAsync(httpContext);
        }
        finally
        {
            image.Dispose();
        }
    }
}
