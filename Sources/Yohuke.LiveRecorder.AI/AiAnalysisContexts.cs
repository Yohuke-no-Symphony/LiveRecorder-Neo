using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

internal sealed class AiAnalysisContexts(IAiEvidenceReader reader)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    internal async Task InitializeAsync(AiAnalysisJob job, AiAnalysisMemory memory, CancellationToken cancellationToken)
    {
        memory.JobId = job.Id;
        memory.SessionId = job.SessionId;
        job.Result.Purpose = job.Purpose;
        if (job.Context.Segments.Count == 0)
        {
            if (job.Snapshot.Segments.Count > 0)
            {
                job.Context.Segments = job.Snapshot.Segments.Select(item => new AiAnalysisSegment
                {
                    Index = item.Index, StartMs = item.StartMs, EndMs = item.EndMs, EvidenceCount = item.EvidenceCount,
                    State = item.EvidenceCount == 0 ? "Empty" : "Pending"
                }).ToList();
            }
            else
            {
                var count = (int)Math.Ceiling(job.Snapshot.DurationMs / AiAnalysisSnapshot.SEGMENT_DURATION_MS);
                for (var index = 0; index < count; index++)
                {
                    var segment = new AiAnalysisSegment
                    {
                        Index = index, StartMs = (double)index * AiAnalysisSnapshot.SEGMENT_DURATION_MS,
                        EndMs = Math.Min(job.Snapshot.DurationMs, (double)(index + 1) * AiAnalysisSnapshot.SEGMENT_DURATION_MS)
                    };
                    var page = await reader.ReadAsync(job.Snapshot, segment.StartMs, segment.EndMs, "All", null, null, 1, cancellationToken);
                    segment.EvidenceCount = page.Items.Count;
                    segment.State = page.Items.Count == 0 && !page.Truncated ? "Empty" : "Pending";
                    job.Context.Segments.Add(segment);
                }
            }
        }
        Advance(job);
    }

    internal static void Advance(AiAnalysisJob job)
    {
        while (job.Context.CurrentSegmentIndex < job.Context.Segments.Count &&
               IsFinished(job.Context.Segments[job.Context.CurrentSegmentIndex]))
        {
            job.Context.CurrentSegmentIndex++;
        }
        job.Context.Phase = job.Context.CurrentSegmentIndex < job.Context.Segments.Count ? "Analyzing" : "Summarizing";
        job.Progress = job.Context.Segments.Count == 0 ? 0 : 95d * job.Context.Segments.Count(IsFinished) / job.Context.Segments.Count;
    }

    internal async Task PrepareAsync(AiAnalysisJob job, AiAnalysisMemory memory, Dictionary<string, AiEvidence> evidence,
        bool compact, CancellationToken cancellationToken, bool force = false)
    {
        var segment = job.Context.Segments[job.Context.CurrentSegmentIndex];
        if (!compact && !force && memory.SegmentIndex == segment.Index && memory.InitialPromptJson.Length > 0)
        {
            return;
        }
        if (memory.SegmentIndex != segment.Index)
        {
            memory.NoProgressRounds = 0;
            memory.NoProgressRecoveryFingerprint = null;
            memory.ValidationRepairPending = false;
            memory.LastToolErrorJson = null;
            memory.LastRestorationFingerprint = null;
        }
        segment.State = "Running";
        var samples = new List<AiEvidence>();
        if (segment.RequiredEvidenceIds.Count == 0)
        {
            var populated = job.Snapshot.Buckets.Where(item => item.OffsetMs >= segment.StartMs && item.OffsetMs < segment.EndMs &&
                item.DanmuCount + item.TranslationCount + item.SuperChatCount > 0).ToArray();
            var count = populated.Length > 0 ? Math.Min(6, populated.Length) : 6;
            for (var index = 0; index < count; index++)
            {
                var bucket = populated.Length > 0 ? populated[(int)((index + 0.5) * populated.Length / count)] : null;
                var center = bucket is null ? segment.StartMs + (segment.EndMs - segment.StartMs) * (index + 0.5) / count
                    : bucket.OffsetMs + Math.Min(60000, bucket.DurationMs) / 2;
                var from = bucket is null ? Math.Max(segment.StartMs, center - 30000) : Math.Max(segment.StartMs, bucket.OffsetMs);
                var to = bucket is null ? Math.Min(segment.EndMs, center + 30000) : Math.Min(segment.EndMs, bucket.OffsetMs + bucket.DurationMs);
                var page = await reader.ReadAsync(job.Snapshot, from, to,
                    job.Purpose == AiJobPurposes.LIVE_MARKERS ? "SuperChat" : "Translation", null, null, 1, cancellationToken);
                if (page.Items.Count == 0 && job.Purpose == AiJobPurposes.LIVE_MARKERS)
                {
                    page = await reader.ReadAsync(job.Snapshot, from, to, "Danmu", null, null, 1, cancellationToken);
                }
                if (page.Items.Count == 0)
                {
                    page = await reader.ReadAsync(job.Snapshot, from, to, "All", null, null, 1, cancellationToken);
                }
                foreach (var item in page.Items)
                {
                    Remember(memory, evidence, item);
                    if (!segment.RequiredEvidenceIds.Contains(item.Id, StringComparer.Ordinal))
                    {
                        segment.RequiredEvidenceIds.Add(item.Id);
                    }
                }
            }
            if (job.Purpose == AiJobPurposes.LIVE_MARKERS)
            {
                var markerHotspots = populated.OrderByDescending(item => item.SuperChatCount * 5 + item.DanmuCount + item.TranslationCount)
                    .Take(6).OrderBy(item => item.OffsetMs);
                foreach (var bucket in markerHotspots)
                {
                    var from = Math.Max(segment.StartMs, bucket.OffsetMs);
                    var to = Math.Min(segment.EndMs, bucket.OffsetMs + bucket.DurationMs);
                    foreach (var kind in new[] { "SuperChat", "Danmu", "Translation" })
                    {
                        var page = await reader.ReadAsync(job.Snapshot, from, to, kind, null, null, 1, cancellationToken);
                        foreach (var item in page.Items)
                        {
                            Remember(memory, evidence, item);
                            if (!segment.RequiredEvidenceIds.Contains(item.Id, StringComparer.Ordinal))
                            {
                                segment.RequiredEvidenceIds.Add(item.Id);
                            }
                        }
                    }
                }
            }
            if (segment.RequiredEvidenceIds.Count == 0)
            {
                var page = await reader.ReadAsync(job.Snapshot, segment.StartMs, segment.EndMs, "All", null, null, 1, cancellationToken);
                if (page.Items.Count == 0 && !page.Truncated)
                {
                    segment.State = "Empty";
                    segment.EvidenceCount = 0;
                    Advance(job);
                    return;
                }
                foreach (var item in page.Items)
                {
                    Remember(memory, evidence, item);
                    segment.RequiredEvidenceIds.Add(item.Id);
                }
            }
        }
        foreach (var id in segment.RequiredEvidenceIds)
        {
            if (evidence.TryGetValue(id, out var item))
            {
                samples.Add(ShortEvidence(item));
            }
        }
        var nearbyTopics = job.Result.Topics.Where(topic => topic.EndMs > segment.StartMs - 60000 && topic.StartMs < segment.EndMs + 60000)
            .TakeLast(6).Select(topic => new
            {
                topic.Id, topic.Title, summary = Short(topic.Summary, 160), topic.StartMs, topic.EndMs,
                evidenceIds = topic.Evidence.Select(item => item.Id)
            });
        var hotspots = job.Snapshot.Buckets.Where(item => item.OffsetMs < segment.EndMs && item.OffsetMs + item.DurationMs > segment.StartMs)
            .OrderByDescending(item => item.DanmuCount + item.TranslationCount + item.SuperChatCount * 5).Take(6).OrderBy(item => item.OffsetMs);
        memory.InitialPromptJson = JsonSerializer.Serialize(new
        {
            job.Purpose, job.Snapshot.Title, job.Snapshot.DurationMs, job.Snapshot.RecordedDurationMs,
            job.Snapshot.OnlineAverage, job.Snapshot.OnlinePeak, job.Snapshot.OnlineObservedMs,
            segment = new { segment.Index, segment.StartMs, segment.EndMs, segment.EvidenceCount, segment.RequiredEvidenceIds },
            totalSegments = job.Context.Segments.Count,
            uniformlySampledEvidence = samples,
            candidateWindows = hotspots,
            toolValidationFeedback = memory.LastToolErrorJson is null ? null : JsonNode.Parse(memory.LastToolErrorJson),
            handoff = new
            {
                topics = nearbyTopics,
                markers = job.Result.LiveMarkers.Where(item => item.OffsetMs >= segment.StartMs && item.OffsetMs < segment.EndMs).TakeLast(6),
                reads = memory.Reads.Where(item => item.FromMs < segment.EndMs && item.ToMs > segment.StartMs).TakeLast(6),
                savedTopicCount = job.Result.Topics.Count,
                savedMarkerCount = job.Result.LiveMarkers.Count,
                suppliedEvidenceCount = evidence.Count,
                note = "Use get_analysis_state to page through saved topic or LiveMarkers text, evidence, and exact read cursors. Truncated pages are not fully read."
            },
            referenceBudget = job.TokenBudget,
            estimatedRemainingTokens = job.TokenBudget - job.PromptTokens - job.CompletionTokens - job.EstimatedTokens
        }, JsonOptions);
        memory.SegmentIndex = segment.Index;
        memory.ConversationJson = "[]";
        memory.LastRawInputEstimate = 0;
        memory.LastPromptTokens = null;
        var restorationFingerprint = RestorationFingerprint(job, memory);
        if ((!compact && !force) || memory.LastRestorationFingerprint != restorationFingerprint)
        {
            memory.StateReadCursors.Clear();
            memory.ContextReadKeys.Clear();
            if (compact || force)
            {
                memory.LastRestorationFingerprint = restorationFingerprint;
            }
        }
        if (compact)
        {
            job.Context.ContextCompactions++;
        }
    }

    internal static string RestorationFingerprint(AiAnalysisJob job, AiAnalysisMemory memory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            job.Context.CurrentSegmentIndex, job.Result.Topics, job.Result.LiveMarkers,
            reads = memory.Reads.Select(item => new { item.Tool, item.FromMs, item.ToMs, item.Kind, item.Query, item.Cursor }),
            evidenceIds = memory.Evidence.Select(item => item.Id)
        }, JsonOptions))));

    internal static void Remember(AiAnalysisMemory memory, Dictionary<string, AiEvidence> evidence, AiEvidence item)
    {
        if (evidence.TryAdd(item.Id, item))
        {
            memory.Evidence.Add(item);
        }
    }

    internal static void RecordRead(AiAnalysisMemory memory, AiAnalysisRead read)
    {
        var index = memory.Reads.FindIndex(item => item.Tool == read.Tool && item.FromMs == read.FromMs && item.ToMs == read.ToMs &&
            item.Kind == read.Kind && item.Query == read.Query && item.Cursor == read.Cursor);
        if (index < 0)
        {
            memory.Reads.Add(read);
        }
        else
        {
            memory.Reads[index] = read;
        }
    }

    internal static bool IsFinished(AiAnalysisSegment segment) => segment.State is "Completed" or "ReviewedNoTopics" or "Empty";

    internal static void Finish(AiAnalysisJob job)
    {
        if (job.Context.Segments.Count == 0 || job.Context.Segments.Any(segment => !IsFinished(segment)))
        {
            throw new InvalidDataException("Unreviewed analysis segments remain.");
        }
        var merged = new List<AiTopic>();
        foreach (var topic in job.Result.Topics.OrderBy(item => item.StartMs))
        {
            var existing = merged.LastOrDefault(item => item.StartMs < topic.EndMs && item.EndMs > topic.StartMs &&
                item.Evidence.Select(value => value.Id).Intersect(topic.Evidence.Select(value => value.Id), StringComparer.Ordinal).Any());
            if (existing is null)
            {
                merged.Add(topic);
                continue;
            }
            existing.StartMs = Math.Min(existing.StartMs, topic.StartMs);
            existing.EndMs = Math.Max(existing.EndMs, topic.EndMs);
            existing.Summary = string.Join("\n", new[] { existing.Summary, topic.Summary }.Distinct(StringComparer.Ordinal));
            existing.Observations = string.Join("\n", new[] { existing.Observations, topic.Observations }.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
            existing.Evidence = existing.Evidence.Concat(topic.Evidence).DistinctBy(item => item.Id).OrderBy(item => item.OffsetMs).ToList();
        }
        job.Result.Topics = merged;
        job.Result.LiveMarkers = job.Result.LiveMarkers.DistinctBy(item => (item.OffsetMs, item.Message))
            .OrderBy(item => item.OffsetMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        RefreshOverview(job);
        job.Result.IsComplete = true;
        job.Result.CompletedAtUtc = DateTime.UtcNow;
        job.Context.Phase = "Completed";
        job.Progress = 100;
        job.State = "Completed";
        job.ErrorCode = null;
    }

    internal static AiEvidence ShortEvidence(AiEvidence item) => new()
    {
        Id = item.Id, OffsetMs = item.OffsetMs, Kind = item.Kind, Text = Short(item.Text, 160)
    };

    internal static void RefreshOverview(AiAnalysisJob job) => job.Result.Overview =
        string.Join("\n\n", job.Context.Segments.Where(segment => segment.Overview.Length > 0)
            .Select(segment => $"{Timestamp(segment.StartMs)}–{Timestamp(segment.EndMs)} {segment.Overview}"));

    private static string Short(string text, int maximum) => text[..Math.Min(text.Length, maximum)];

    private static string Timestamp(double milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }
}
