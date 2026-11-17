using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

public sealed class AiSnapshotBuilderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProjectsFixedSourcesAndDeduplicatesEvidenceWithoutChangingStatistics()
    {
        Directory.CreateDirectory(directory);
        var events = new RecordedEventEnvelope[]
        {
            new() { Sequence = 1, EventId = "outside", OffsetMs = 0, Text = "outside" },
            new() { Sequence = 2, EventId = "translation", OffsetMs = 1000, Text = "EN: hello" },
            new() { Sequence = 3, EventId = "translation", OffsetMs = 2000, Text = "duplicate" },
            new() { Sequence = 4, EventId = "sc", Kind = "SuperChat", OffsetMs = 3000, Text = "question", Amount = 30, TransactionId = "sc" },
            new() { Sequence = 5, EventId = "end", OffsetMs = 4000, Text = "excluded" }
        };
        var path = Path.Combine(directory, "raw.jsonl");
        await File.WriteAllLinesAsync(path, events.Select(item => JsonSerializer.Serialize(item)));
        var manifest = new AiInputManifest
        {
            Fingerprint = "fixed", DurationMs = 60000, RecordedDurationMs = 3000, TranslationPattern = "^EN:",
            Sources = [new() { Id = "first", StartOffsetMs = 10000, FromMs = 1000, ToMs = 4000, Length = new FileInfo(path).Length }]
        };

        var snapshot = await new AiSnapshotBuilder().BuildAsync(new() { SessionId = "session" }, manifest,
            new Dictionary<string, string> { ["first"] = path }, directory);
        var evidence = await new AiFileEvidenceReader(directory).ReadAsync(snapshot, 0, snapshot.DurationMs, null, null, null, 40, default);

        Assert.Equal(2, snapshot.EvidenceCount);
        Assert.Equal(new[] { 11000d, 13000d }, evidence.Items.Select(item => item.OffsetMs));
        Assert.Equal(new[] { "Translation", "SuperChat" }, evidence.Items.Select(item => item.Kind));
        Assert.Equal("fixed", snapshot.SourceFingerprint);
        Assert.Equal(3000, snapshot.RecordedDurationMs);
        Assert.Equal(2, Assert.Single(snapshot.Segments).EvidenceCount);
        Assert.Equal(30, Assert.Single(snapshot.Buckets).SuperChatAmount);
        Assert.Equal(1, Assert.Single(snapshot.Buckets).TranslationCount);
    }

    [Fact]
    public async Task PreparedSnapshotRetainsLegacyMetricsAndDoesNotProjectEvidenceAgain()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.jsonl");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AiEvidence { Id = "0:3", Kind = "Translation", OffsetMs = 1200, Text = "legacy" }) + "\n");
        var execution = new AiExecutionState { Snapshot = new() { DurationMs = 5000, EvidenceCount = 1, OnlinePeak = 123, Buckets = [new() { DanmuCount = 9 }] } };

        var snapshot = await new AiSnapshotBuilder().BuildAsync(execution,
            new() { PreparedSnapshot = true, Sources = [new() { Id = "legacy", Length = new FileInfo(path).Length }] },
            new Dictionary<string, string> { ["legacy"] = path }, directory);

        Assert.Equal(123, snapshot.OnlinePeak);
        Assert.Equal(9, Assert.Single(snapshot.Buckets).DanmuCount);
        Assert.Single((await new AiFileEvidenceReader(directory).ReadAsync(snapshot, 0, 5000, null, null, null, 40, default)).Items);
        Assert.Empty(execution.Snapshot.EvidenceRelativePath);
    }

    [Fact]
    public async Task RejectsDownloadedSourcesThatDoNotMatchFixedLength()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "short.jsonl");
        await File.WriteAllTextAsync(path, "{}\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => new AiSnapshotBuilder().BuildAsync(new(),
            new() { DurationMs = 1000, TranslationPattern = "^EN:", Sources = [new() { Id = "source", Length = 100 }] },
            new Dictionary<string, string> { ["source"] = path }, directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
