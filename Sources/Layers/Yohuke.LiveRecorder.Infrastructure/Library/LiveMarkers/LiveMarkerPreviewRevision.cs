using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain.LiveMarkers;

namespace Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

/// <summary>标识不可变记录正文、解析快照和录像时间投影的浏览器缓存版本。</summary>
public static class LiveMarkerPreviewRevision
{
    private const string FORMAT_VERSION = "live-marker-preview-v1";

    /// <summary>在内容、解析时区、整体校准或录像起点变化时生成不同版本。</summary>
    public static string Create(LiveMarkerFileMetadata file, DateTime startedAtUtc)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion = FORMAT_VERSION,
            file.ContentHash,
            file.SourceTimeZoneId,
            file.EntryCount,
            file.CalibrationMs,
            StartedAtTicks = startedAtUtc.Ticks
        });
        return Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }
}
