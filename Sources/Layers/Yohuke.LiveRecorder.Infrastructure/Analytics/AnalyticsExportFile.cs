namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>由调用方释放的 Excel 下载流，关闭流即删除临时文件。</summary>
public sealed class AnalyticsExportFile : IDisposable, IAsyncDisposable
{
    public const string CONTENT_TYPE = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public Stream Content { get; }
    public string FileName { get; }

    internal AnalyticsExportFile(Stream content, string fileName)
    {
        Content = content;
        FileName = fileName;
    }

    /// <summary>释放下载流并删除临时文件。</summary>
    public void Dispose()
    {
        Content.Dispose();
    }

    /// <summary>异步释放下载流并删除临时文件。</summary>
    public ValueTask DisposeAsync()
    {
        return Content.DisposeAsync();
    }
}
