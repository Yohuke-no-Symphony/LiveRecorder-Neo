using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>通过有界包扫描验证独立媒体产物和原片参数。</summary>
public interface ITranscodeMediaInspector
{
    /// <summary>扫描媒体包统计实际时长和码率，不依赖 Worker 声明。</summary>
    Task<TranscodeMediaInfo> InspectAsync(string path, CancellationToken cancellationToken = default);
}
