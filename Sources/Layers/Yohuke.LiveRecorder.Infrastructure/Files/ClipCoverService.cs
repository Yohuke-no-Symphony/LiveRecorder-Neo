using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>从切片成品抽取封面，按源文件版本复用图片缓存。</summary>
public sealed class ClipCoverService(IVideoFrameExtractor frames, ArtworkCache artwork, ILogger<ClipCoverService> logger)
{
    /// <summary>读取已鉴权切片的封面，源文件变化时重新抽帧。</summary>
    public async Task<ArtworkCacheLease?> GetAsync(string clipId, string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            var length = source.Length;
            var lastWrite = source.LastWriteTimeUtc;
            var identity = FormattableString.Invariant($"clip-cover:{length}:{lastWrite.Ticks}:{source.FullName}");
            return await artwork.GetGeneratedJpegAsync(identity, async (destination, token) =>
            {
                var request = new VideoFrameRequest
                {
                    SourcePath = source.FullName,
                    IsClosed = true,
                    OutputPath = destination
                };
                if (!await frames.TryExtractAsync(request, token))
                {
                    return false;
                }
                source.Refresh();
                return source.Exists && source.Length == length && source.LastWriteTimeUtc == lastWrite;
            }, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
                                      && error is IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException)
        {
            logger.LogDebug(error, "Could not generate a cover for clip {ClipId}", clipId);
            return null;
        }
    }
}
