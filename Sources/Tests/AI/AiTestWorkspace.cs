using System.Text.Json;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestWorkspace : IDisposable
{
    private string Root { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-ai-tests", Guid.NewGuid().ToString("N"));
    internal AppPaths Paths { get; }
    internal AnalyticsService Analytics { get; } = new(new());
    internal AiArchiveDataSource Data { get; }
    internal ServerSettings Settings { get; } = new() { TranslationDanmuPattern = "^T:", Ai = new() { Enabled = true, Model = "mock" } };

    internal AiTestWorkspace()
    {
        Paths = new(Path.Combine(Root, "data"), Path.Combine(Root, "records"));
        Paths.EnsureDirectories();
        Data = new(new(), Paths, Analytics);
    }

    internal static SessionDetail Session(double durationMs = 120000) => new()
    {
        DurationMs = durationMs,
        Session = new()
        {
            DirectoryPath = "source", Title = "直播测试", StartedAtUtc = DateTime.UnixEpoch,
            EndedAtUtc = DateTime.UnixEpoch.AddMilliseconds(durationMs), State = "Completed"
        }
    };

    internal async Task WriteAsync(string directory, params RecordedEventEnvelope[] events)
    {
        var path = Path.Combine(Paths.RecordsDirectory, directory, "弹幕", "事件.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, string.Concat(events.Select((item, index) =>
        {
            item.Sequence = index + 1;
            item.EventId = directory + ":" + item.Sequence;
            return JsonSerializer.Serialize(item, JsonFiles.Options) + "\n";
        })));
    }

    public void Dispose() => Directory.Delete(Root, true);
}
