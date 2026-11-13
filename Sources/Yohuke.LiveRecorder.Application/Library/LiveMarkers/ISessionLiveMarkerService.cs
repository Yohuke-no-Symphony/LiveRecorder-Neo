namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>场次共享记录文件的读取、原生上传和归属管理边界。</summary>
public interface ISessionLiveMarkerService
{
    /// <summary>列出指定场次的共享文件。</summary>
    Task<LiveMarkerFileListResponse> ListAsync(string sessionId, LiveMarkerActor actor,
        CancellationToken cancellationToken = default);

    /// <summary>读取原记录并映射至当前场次时间轴。</summary>
    Task<LiveMarkerFileResponse> GetAsync(string sessionId, string fileId, LiveMarkerActor actor,
        CancellationToken cancellationToken = default);

    /// <summary>验证并原子发布一个原生 YAML 文件。</summary>
    Task<LiveMarkerFileSummary> UploadAsync(string sessionId, Stream content, string fileName, string? timeZoneId,
        long calibrationMs, LiveMarkerActor actor, CancellationToken cancellationToken = default);

    /// <summary>修改当前文件的整体校准。</summary>
    Task<LiveMarkerFileSummary> AlignAsync(string sessionId, string fileId, long calibrationMs,
        LiveMarkerActor actor, CancellationToken cancellationToken = default);

    /// <summary>永久删除当前文件，不影响独立副本。</summary>
    Task DeleteAsync(string sessionId, string fileId, LiveMarkerActor actor,
        CancellationToken cancellationToken = default);
}
