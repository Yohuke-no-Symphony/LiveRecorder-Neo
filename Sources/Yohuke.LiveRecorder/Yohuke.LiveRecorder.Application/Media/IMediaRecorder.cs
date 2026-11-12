namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>将直播流归档为可索引的媒体分段。</summary>
public interface IMediaRecorder
{
    /// <summary>录制至源结束或取消，并关闭已创建的文件。</summary>
    Task RecordAsync(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken);
}
