using System.Globalization;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>以同一事件观测快照导出全量统计与赠送明细，不读取或覆盖页面统计缓存。</summary>
public sealed class AnalyticsExportService(AnalyticsService analytics)
{
    /// <summary>导出整场或半开选区的 Excel 工作簿；调用方负责释放返回的文件流。</summary>
    public Task<AnalyticsExportFile> ExportAsync(SessionDetail detail, string storageRoot, string translationPattern,
        double? fromMs = null, double? toMs = null, string? language = null, CancellationToken cancellationToken = default)
    {
        return ExportAsync(detail, storageRoot, translationPattern, fromMs, toMs, language, null, cancellationToken);
    }

    /// <summary>将主播显示名写入工作簿和下载文件名，再导出同一批事件的全量数据。</summary>
    public async Task<AnalyticsExportFile> ExportAsync(SessionDetail detail, string storageRoot, string translationPattern,
        double? fromMs, double? toMs, string? language, string? streamerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fromMs is { } from && (!double.IsFinite(from) || from < 0 || from >= detail.DurationMs)
            || toMs is { } to && (!double.IsFinite(to) || to <= (fromMs ?? 0) || to > detail.DurationMs))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
        }
        var data = await analytics.BuildExportAsync(detail, storageRoot, translationPattern, fromMs, toMs, cancellationToken);
        data.Report.CoverageGaps = Math.Max(data.Report.CoverageGaps,
            detail.Gaps.Count(gap => gap.StartMs < data.Report.ToMs && gap.EndMs > data.Report.FromMs));
        cancellationToken.ThrowIfCancellationRequested();
        var labels = new AnalyticsExportLabels(language);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 65536,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        var path = Path.Combine(Path.GetTempPath(), "live-recorder-analytics-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var stream = new FileStream(path, options);
        try
        {
            using (var writer = new AnalyticsSpreadsheetWriter(stream, labels, cancellationToken))
            {
                new AnalyticsExportTables(labels).Write(writer, detail, data, streamerName);
            }
            await stream.FlushAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            var name = MediaPaths.SafeName(string.IsNullOrWhiteSpace(streamerName) ? detail.Session.Title : streamerName, 40);
            var date = detail.Session.StartedAtUtc.ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture);
            var range = fromMs.HasValue || toMs.HasValue ? RangeName(data.Report.FromMs) + "-" + RangeName(data.Report.ToMs) : labels["fullSession"];
            return new(stream, name + "_" + date + "_" + labels["analytics"] + "_" + range + ".xlsx");
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static string RangeName(double milliseconds)
    {
        var hours = Math.Floor(milliseconds / 3600000);
        var minutes = Math.Floor(milliseconds / 60000) % 60;
        var seconds = milliseconds / 1000 % 60;
        return hours.ToString("00", CultureInfo.InvariantCulture) + "h" + minutes.ToString("00", CultureInfo.InvariantCulture) + "m" + seconds.ToString("00.000", CultureInfo.InvariantCulture) + "s";
    }
}
