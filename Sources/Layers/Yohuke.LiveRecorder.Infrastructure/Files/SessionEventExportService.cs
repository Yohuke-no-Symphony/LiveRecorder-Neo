using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>从场次事件归档生成按时间排列的弹幕或同传文本。</summary>
public sealed class SessionEventExportService(EventArchiveReader reader)
{
    /// <summary>导出完整归档记录，匹配正文正则时只导出同传。调用方负责释放返回的流。</summary>
    public async Task<(Stream Content, string FileName)> ExportAsync(BroadcastSession session, string storageRoot,
        Regex? translationPattern = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var formatter = new SessionEventTextFormatter(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        var translationsOnly = translationPattern is not null;
        var sources = SessionGroupingService.Sources(session).Select(source =>
        {
            var path = MediaPaths.Resolve(storageRoot, Path.Combine(source.DirectoryPath, "弹幕", "事件.jsonl"));
            var file = new FileInfo(path);
            return (Source: source, Path: path, Length: file.Exists ? file.Length : 0);
        }).ToArray();

        var lines = new List<(double OffsetMs, string Text)>();
        foreach (var (source, path, length) in sources)
        {
            if (length == 0 || source.ToMs <= source.FromMs)
            {
                continue;
            }
            await foreach (var item in reader.EnumerateAsync(path, length, cancellationToken))
            {
                var offsetMs = item.OffsetMs + source.StartOffsetMs;
                if (!double.IsFinite(offsetMs) || offsetMs < 0 || item.OffsetMs < source.FromMs || item.OffsetMs >= source.ToMs)
                {
                    continue;
                }
                if (translationPattern is not null && (item.Kind != "Danmu" || !translationPattern.IsMatch(item.Text ?? "")))
                {
                    continue;
                }
                if (formatter.FormatEvent(item, translationsOnly) is { } line)
                {
                    lines.Add((offsetMs, line));
                }
            }
        }

        var label = formatter.Title(translationsOnly);
        var date = session.StartedAtUtc.ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture);
        var name = $"{MediaPaths.SafeName(session.Title, 40)}_{date}_{label}.txt";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"live-recorder-events-{Guid.NewGuid():N}.txt"), options);
        try
        {
            await using (var output = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true) { NewLine = "\n" })
            {
                await output.WriteLineAsync(formatter.FormatHeader(session, label).AsMemory(), cancellationToken);
                if (lines.Count == 0)
                {
                    await output.WriteLineAsync(formatter.Empty(translationsOnly).AsMemory(), cancellationToken);
                }
                foreach (var (offsetMs, line) in lines.OrderBy(item => item.OffsetMs))
                {
                    var record = $"[{FormatTime(offsetMs)}] {line}";
                    await output.WriteLineAsync(record.AsMemory(), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            stream.Position = 0;
            return (stream, name);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static string FormatTime(double offsetMs)
    {
        var milliseconds = Math.Floor(offsetMs);
        var hours = Math.Floor(milliseconds / 3_600_000);
        var minutes = Math.Floor(milliseconds / 60_000) % 60;
        var seconds = Math.Floor(milliseconds / 1000) % 60;
        return FormattableString.Invariant($"{hours:00}:{minutes:00}:{seconds:00}.{milliseconds % 1000:000}");
    }
}
