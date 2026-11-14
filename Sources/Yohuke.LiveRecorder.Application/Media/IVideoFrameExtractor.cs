namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>从已提交的录像数据提取独立的 JPEG 封面。</summary>
public interface IVideoFrameExtractor
{
    /// <summary>有可用视频帧时写入图片，否则等待后续录制数据；取消时不发布输出。</summary>
    Task<bool> TryExtractAsync(VideoFrameRequest request, CancellationToken cancellationToken = default);
}
