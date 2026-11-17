using System.Text.Json;
using Yohuke.LiveRecorder.AI;
using Yohuke.LiveRecorder.AI.Contracts;
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
    internal AiFileEvidenceReader Evidence { get; }
    internal ServerSettings Settings { get; } = new() { TranslationDanmuPattern = "^T:", Ai = new() { Enabled = true, Model = "mock" } };

    internal AiTestWorkspace()
    {
        Paths = new(Path.Combine(Root, "data"), Path.Combine(Root, "records"));
        Paths.EnsureDirectories();
        Data = new(Paths);
        Evidence = new(Paths.DataDirectory);
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

    internal Task<AiAnalysisSnapshot> PrepareAsync(SessionDetail detail, CancellationToken cancellationToken = default)
    {
        var input = new AiInputManifestFactory(Paths, Data).Create(detail, Settings, out var paths);
        var sources = input.Sources.Select((source, index) => new
        {
            source.Id, Path = Path.Combine(Paths.RecordsDirectory, paths[index])
        }).ToDictionary(source => source.Id, source => source.Path);
        return BuildSnapshotAsync(new() { JobId = Guid.NewGuid().ToString("N"), SessionId = detail.Session.Id }, input, sources, cancellationToken);
    }

    internal async Task<AiAnalysisSnapshot> BuildSnapshotAsync(AiExecutionState execution, AiInputManifest input,
        IReadOnlyDictionary<string, string> sources, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Paths.DataDirectory, "ai", "snapshots", execution.JobId);
        var sourcesDirectory = Path.Combine(directory, "sources");
        Directory.CreateDirectory(sourcesDirectory);
        var downloaded = new Dictionary<string, string>();
        foreach (var source in input.Sources)
        {
            var destination = Path.Combine(sourcesDirectory, source.Id + ".jsonl");
            await using var original = File.OpenRead(sources[source.Id]);
            await using var output = File.Create(destination);
            var remaining = source.Length;
            var buffer = new byte[65536];
            while (remaining > 0)
            {
                var count = await original.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException("The fixed AI source ended before its manifest length.");
                }
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }
            downloaded.Add(source.Id, destination);
        }
        var snapshot = await new AiSnapshotBuilder().BuildAsync(execution, input, downloaded, directory, cancellationToken);
        snapshot.EvidenceRelativePath = Path.GetRelativePath(Paths.DataDirectory, Path.Combine(directory, snapshot.EvidenceRelativePath));
        return snapshot;
    }

    public void Dispose() => Directory.Delete(Root, true);
}
