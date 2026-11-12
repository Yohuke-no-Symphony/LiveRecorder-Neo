namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>执行媒体探测、修复和导出。</summary>
public interface IMediaProcessor
{
    /// <summary>探测文件的媒体参数。</summary>
    Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>修复时间码并写入独立输出。</summary>
    Task<MediaRepairResult> RepairAsync(string inputPath, string outputPath, IProgress<double> progress, CancellationToken cancellationToken);
    /// <summary>导出固定范围，精确模式保留断流时长。</summary>
    Task<MediaExportResult> ExportAsync(MediaExportRequest request, IProgress<double> progress, CancellationToken cancellationToken);
}
