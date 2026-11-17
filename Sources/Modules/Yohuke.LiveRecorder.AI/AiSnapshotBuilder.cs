using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;

namespace Yohuke.LiveRecorder.AI;

/// <summary>在执行节点将已下载的固定事件来源整理为模型可读取的精简证据及统计快照。</summary>
public sealed class AiSnapshotBuilder
{
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>复用共同统计规则构建快照；升级前已准备的快照只复制证据，不重新投影。</summary>
    public async Task<AiAnalysisSnapshot> BuildAsync(AiExecutionState execution, AiInputManifest manifest,
        IReadOnlyDictionary<string, string> sourcePaths, string outputDirectory, CancellationToken cancellationToken = default)
    {
        foreach (var source in manifest.Sources)
        {
            if (!sourcePaths.TryGetValue(source.Id, out var path) || new FileInfo(path).Length != source.Length)
            {
                throw new InvalidDataException("The downloaded AI input does not match its fixed length.");
            }
        }
        Directory.CreateDirectory(outputDirectory);
        const string EVIDENCE_PATH = "evidence.jsonl";
        var output = Path.Combine(outputDirectory, EVIDENCE_PATH);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (manifest.PreparedSnapshot)
            {
                if (manifest.Sources.Count != 1)
                {
                    throw new InvalidDataException("A prepared AI snapshot must have one evidence source.");
                }
                await using (var input = File.OpenRead(sourcePaths[manifest.Sources[0].Id]))
                await using (var destination = CreateOutput(temporary))
                {
                    await input.CopyToAsync(destination, cancellationToken);
                    destination.Flush(true);
                }
                var legacy = JsonSerializer.Deserialize<AiAnalysisSnapshot>(JsonSerializer.Serialize(execution.Snapshot, JsonOptions), JsonOptions)!;
                legacy.EvidenceRelativePath = EVIDENCE_PATH;
                File.Move(temporary, output, true);
                return legacy;
            }
            if (!double.IsFinite(manifest.DurationMs) || manifest.DurationMs < 0)
            {
                throw new InvalidDataException("The AI input duration is invalid.");
            }
            var snapshot = new AiAnalysisSnapshot
            {
                SessionId = execution.SessionId, Title = manifest.Title, StartedAtUtc = manifest.StartedAtUtc,
                DurationMs = manifest.DurationMs, RecordedDurationMs = manifest.RecordedDurationMs,
                SourceFingerprint = manifest.Fingerprint, TranslationPattern = manifest.TranslationPattern, EvidenceRelativePath = EVIDENCE_PATH,
                Segments = Enumerable.Range(0, checked((int)Math.Ceiling(manifest.DurationMs / AiAnalysisSnapshot.SEGMENT_DURATION_MS)))
                    .Select(index => new AiAnalysisSegment
                    {
                        Index = index, StartMs = (double)index * AiAnalysisSnapshot.SEGMENT_DURATION_MS,
                        EndMs = Math.Min(manifest.DurationMs, (double)(index + 1) * AiAnalysisSnapshot.SEGMENT_DURATION_MS)
                    }).ToList()
            };
            var pattern = TranslationDanmuPattern.Create(manifest.TranslationPattern);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            await using (var stream = CreateOutput(temporary))
            {
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, true))
                {
                    for (var index = 0; index < manifest.Sources.Count; index++)
                    {
                        var source = manifest.Sources[index];
                        await foreach (var item in ReadEventsAsync(sourcePaths[source.Id], source.Length, cancellationToken))
                        {
                            if (item.OffsetMs < source.FromMs || source.ToMs is { } end && item.OffsetMs >= end)
                            {
                                continue;
                            }
                            var offset = item.OffsetMs + source.StartOffsetMs;
                            if (offset < 0 || offset >= manifest.DurationMs || item.Kind is not ("Danmu" or "SuperChat"))
                            {
                                continue;
                            }
                            if (!string.IsNullOrEmpty(item.EventId) && !identities.Add(item.EventId) || string.IsNullOrWhiteSpace(item.Text))
                            {
                                continue;
                            }
                            var evidence = new AiEvidence
                            {
                                Id = $"{index}:{item.Sequence}", OffsetMs = offset,
                                Kind = item.Kind == "Danmu" && pattern.IsMatch(item.Text) ? "Translation" : item.Kind,
                                Text = item.Text[..Math.Min(item.Text.Length, 400)]
                            };
                            await writer.WriteLineAsync(JsonSerializer.Serialize(evidence, JsonOptions).AsMemory(), cancellationToken);
                            snapshot.EvidenceCount++;
                            snapshot.Segments[(int)(offset / AiAnalysisSnapshot.SEGMENT_DURATION_MS)].EvidenceCount++;
                        }
                    }
                }
                stream.Flush(true);
            }
            var sources = manifest.Sources.Select(source => new AnalyticsProjectionSource
            {
                OffsetMs = source.StartOffsetMs, FromMs = source.FromMs, ToMs = source.ToMs,
                ReadAsync = token => ReadEventsAsync(sourcePaths[source.Id], source.Length, token)
            }).ToArray();
            var report = (await new AnalyticsProjectionEngine().BuildAsync(sources,
                new() { FixedEndMs = manifest.DurationMs, TranslationPattern = pattern }, cancellationToken)).Report;
            var translations = report.Translations.Buckets.ToDictionary(bucket => bucket.OffsetMs, bucket => bucket.Count);
            var buckets = report.Buckets.ToDictionary(bucket => bucket.OffsetMs);
            snapshot.Buckets = buckets.Keys.Union(translations.Keys).Order().Select(offset =>
            {
                var bucket = buckets.GetValueOrDefault(offset);
                return new AiMetricBucket
                {
                    OffsetMs = offset, DurationMs = bucket?.DurationMs ?? Math.Min(60000, manifest.DurationMs - offset),
                    DanmuCount = bucket?.DanmuCount ?? 0, TranslationCount = translations.GetValueOrDefault(offset),
                    SuperChatCount = bucket?.SuperChatCount ?? 0, SuperChatAmount = bucket?.SuperChatAmount ?? 0,
                    OnlineAverage = bucket?.OnlineAverage, OnlinePeak = bucket?.OnlinePeak, OnlineObservedMs = bucket?.OnlineObservedMs ?? 0
                };
            }).ToList();
            snapshot.OnlineAverage = report.Online.Average;
            snapshot.OnlinePeak = report.Online.Peak;
            snapshot.OnlineObservedMs = report.Online.ObservedMs;
            snapshot.OnlineCoverageRate = report.Online.CoverageRate;
            File.Move(temporary, output, true);
            return snapshot;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static FileStream CreateOutput(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new(path, options);
    }

    private static async IAsyncEnumerable<RecordedEventEnvelope> ReadEventsAsync(string path, long length,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length != length)
        {
            throw new InvalidDataException("The fixed AI input length changed.");
        }
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            RecordedEventEnvelope? item;
            try
            {
                item = JsonSerializer.Deserialize<RecordedEventEnvelope>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (item is { Sequence: > 0 } && double.IsFinite(item.OffsetMs))
            {
                yield return item;
            }
        }
    }
}
