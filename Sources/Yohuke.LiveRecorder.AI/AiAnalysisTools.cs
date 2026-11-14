using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

internal sealed class AiAnalysisTools(IAiEvidenceReader reader)
{
    internal async Task<JsonNode> ExecuteAsync(string name, JsonObject args, AiAnalysisJob job, AiAnalysisMemory memory,
        Dictionary<string, AiEvidence> evidence, CancellationToken cancellationToken)
    {
        if (name == "submit_analysis")
        {
            if (job.Purpose != AiJobPurposes.ANALYSIS)
            {
                throw new InvalidDataException();
            }
            Submit(args, job, evidence);
            var segment = job.Context.Segments[job.Context.CurrentSegmentIndex];
            return new JsonObject { ["savedTopics"] = segment.TopicIds.Count, ["segmentComplete"] = AiAnalysisContexts.IsFinished(segment), ["complete"] = false };
        }
        if (name == "submit_live_markers")
        {
            if (job.Purpose != AiJobPurposes.LIVE_MARKERS)
            {
                throw new InvalidDataException();
            }
            AiLiveMarkerSubmissions.Submit(args, job, evidence);
            var segment = job.Context.Segments[job.Context.CurrentSegmentIndex];
            return new JsonObject { ["savedMarkers"] = segment.LiveMarkerIds.Count, ["segmentComplete"] = AiAnalysisContexts.IsFinished(segment), ["complete"] = false };
        }
        if (name == "get_analysis_state")
        {
            return ReadState(args, job, memory);
        }
        if (name is not ("get_metrics" or "get_messages" or "search_messages"))
        {
            throw new AiToolValidationException("function.name", "Use only the tools supplied for this job purpose.");
        }
        var from = Required<double>(args, "fromMs");
        var to = Required<double>(args, "toMs");
        ValidateRange(from, to, job.Snapshot.DurationMs);
        if (name == "get_metrics")
        {
            var buckets = job.Snapshot.Buckets.Where(item => item.OffsetMs < to && item.OffsetMs + item.DurationMs > from).ToArray();
            AiAnalysisContexts.RecordRead(memory, new() { Tool = name, FromMs = from, ToMs = to, Truncated = buckets.Length > 60 });
            if (buckets.Length > 0)
            {
                RememberContextRead(memory, name, from, to, null, null, null, 60);
            }
            return JsonSerializer.SerializeToNode(new { fromMs = from, toMs = to, buckets = buckets.Take(60), truncated = buckets.Length > 60 }, AiAnalysisContexts.JsonOptions)!;
        }
        if (to - from > (name == "search_messages" ? 1800000 : 600000))
        {
            throw new InvalidDataException();
        }
        var query = name == "search_messages" ? Required<string>(args, "query") : null;
        if (query is not null && (query.Length is < 1 or > 80))
        {
            throw new InvalidDataException();
        }
        var kind = args["kind"]?.GetValue<string>() ?? "All";
        if (kind is not ("All" or "Translation" or "Danmu" or "SuperChat"))
        {
            throw new InvalidDataException();
        }
        var limit = args["limit"]?.GetValue<int>() ?? 24;
        if (limit is < 1 or > 40)
        {
            throw new InvalidDataException();
        }
        var cursor = args["cursor"]?.GetValue<string>();
        var page = await reader.ReadAsync(job.Snapshot, from, to, kind, query, cursor, limit, cancellationToken);
        foreach (var item in page.Items)
        {
            AiAnalysisContexts.Remember(memory, evidence, item);
        }
        AiAnalysisContexts.RecordRead(memory, new()
        {
            Tool = name, FromMs = from, ToMs = to, Kind = kind, Query = query, Cursor = cursor,
            NextCursor = page.NextCursor, Truncated = page.Truncated, EvidenceIds = page.Items.Select(item => item.Id).ToList()
        });
        if (page.Items.Count > 0)
        {
            RememberContextRead(memory, name, from, to, kind, query, cursor, limit);
        }
        return JsonSerializer.SerializeToNode(page, AiAnalysisContexts.JsonOptions)!;
    }

    private static void RememberContextRead(AiAnalysisMemory memory, string tool, double from, double to, string? kind, string? query, string? cursor, int limit)
    {
        var key = JsonSerializer.Serialize(new { tool, from, to, kind, query, cursor, limit });
        if (!memory.ContextReadKeys.Contains(key, StringComparer.Ordinal))
        {
            memory.ContextReadKeys.Add(key);
        }
    }

    private static JsonNode ReadState(JsonObject args, AiAnalysisJob job, AiAnalysisMemory memory)
    {
        var cursor = args["cursor"]?.GetValue<string>() ?? "0";
        if (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
        {
            throw new InvalidDataException();
        }
        var limit = args["limit"]?.GetValue<int>() ?? 1;
        if (limit is < 1 or > 4)
        {
            throw new InvalidDataException();
        }
        var kind = args["kind"]?.GetValue<string>() ?? "Topics";
        var result = new JsonObject();
        int count;
        switch (kind)
        {
            case "LiveMarkers":
                result["markers"] = JsonSerializer.SerializeToNode(job.Result.LiveMarkers.Skip(offset).Take(limit), AiAnalysisContexts.JsonOptions);
                count = job.Result.LiveMarkers.Count;
                break;
            case "Topics":
                var topics = job.Result.Topics.Skip(offset).Take(limit).ToArray();
                result["topics"] = JsonSerializer.SerializeToNode(topics, AiAnalysisContexts.JsonOptions);
                count = job.Result.Topics.Count;
                break;
            case "Reads":
                result["reads"] = JsonSerializer.SerializeToNode(memory.Reads.Skip(offset).Take(limit), AiAnalysisContexts.JsonOptions);
                count = memory.Reads.Count;
                break;
            case "Evidence":
                result["evidence"] = JsonSerializer.SerializeToNode(memory.Evidence.Skip(offset).Take(limit), AiAnalysisContexts.JsonOptions);
                count = memory.Evidence.Count;
                break;
            default:
                throw new InvalidDataException();
        }
        var next = (long)offset + limit;
        result["nextCursor"] = next < count ? next.ToString(CultureInfo.InvariantCulture) : null;
        result["total"] = count;
        var stateCursor = kind + ":" + cursor + ":" + limit;
        if (offset < count && !memory.StateReadCursors.Contains(stateCursor, StringComparer.Ordinal))
        {
            memory.StateReadCursors.Add(stateCursor);
        }
        return result;
    }

    private static void Submit(JsonObject args, AiAnalysisJob job, IReadOnlyDictionary<string, AiEvidence> evidence)
    {
        var segment = job.Context.Segments[job.Context.CurrentSegmentIndex];
        var overview = Text(args, "overview", 2000);
        var completed = Required<bool>(args, "complete");
        var incoming = args["topics"] as JsonArray ?? throw new InvalidDataException();
        if (incoming.Count > 8)
        {
            throw new InvalidDataException();
        }
        var merged = job.Result.Topics.ToDictionary(topic => topic.Id, StringComparer.Ordinal);
        var segmentIds = new HashSet<string>(segment.TopicIds, StringComparer.Ordinal);
        foreach (var value in incoming)
        {
            var item = value as JsonObject ?? throw new InvalidDataException();
            var localId = Text(item, "id", 80);
            var prefix = segment.Index == 0 ? "" : $"s{segment.Index}:";
            var inputPrefix = $"s{segment.Index}:";
            if (localId.StartsWith(inputPrefix, StringComparison.Ordinal))
            {
                localId = localId[inputPrefix.Length..];
            }
            if (!Regex.IsMatch(localId, "^[a-zA-Z0-9_-]{1,64}$"))
            {
                throw new AiToolValidationException("topics[].id", "Use 1 to 64 ASCII letters, digits, underscores or hyphens, optionally with the current segment's sN: prefix.");
            }
            var topic = new AiTopic
            {
                Id = prefix + localId, Title = Text(item, "title", 120), Summary = Text(item, "summary", 1200),
                Observations = Text(item, "observations", 800), StartMs = Required<double>(item, "startMs"), EndMs = Required<double>(item, "endMs")
            };
            ValidateRange(topic.StartMs, topic.EndMs, job.Snapshot.DurationMs);
            if (string.IsNullOrWhiteSpace(topic.Title) || string.IsNullOrWhiteSpace(topic.Summary) ||
                topic.StartMs >= segment.EndMs || topic.EndMs <= segment.StartMs)
            {
                throw new InvalidDataException();
            }
            var ids = item["evidenceIds"] as JsonArray ?? throw new InvalidDataException();
            if (ids.Count is < 1 or > 8)
            {
                throw new InvalidDataException();
            }
            foreach (var id in ids.Select(Value<string>).Distinct(StringComparer.Ordinal))
            {
                if (!evidence.TryGetValue(id, out var supplied) || supplied.OffsetMs < topic.StartMs || supplied.OffsetMs >= topic.EndMs)
                {
                    throw new AiToolValidationException("topics[].evidenceIds", "Every cited ID must have been supplied and its offset must lie in [startMs,endMs).");
                }
                topic.Evidence.Add(supplied);
            }
            var duplicate = merged.Values.FirstOrDefault(existing => segmentIds.Contains(existing.Id) && existing.StartMs == topic.StartMs &&
                existing.EndMs == topic.EndMs && existing.Title == topic.Title && existing.Evidence.Select(entry => entry.Id).SequenceEqual(topic.Evidence.Select(entry => entry.Id)));
            if (duplicate is not null)
            {
                topic.Id = duplicate.Id;
            }
            merged[topic.Id] = topic;
            segmentIds.Add(topic.Id);
        }
        var reviewed = new HashSet<string>(segmentIds.SelectMany(id => merged[id].Evidence.Select(item => item.Id)), StringComparer.Ordinal);
        if (args["reviewedEvidenceIds"] is JsonArray reviewedIds)
        {
            if (reviewedIds.Count > 40)
            {
                throw new InvalidDataException();
            }
            foreach (var id in reviewedIds.Select(Value<string>))
            {
                if (!evidence.TryGetValue(id, out var supplied) || supplied.OffsetMs < segment.StartMs || supplied.OffsetMs >= segment.EndMs)
                {
                    throw new InvalidDataException();
                }
                reviewed.Add(id);
            }
        }
        if (completed && (string.IsNullOrWhiteSpace(overview) || segment.RequiredEvidenceIds.Count == 0 ||
                          segment.RequiredEvidenceIds.Any(id => !reviewed.Contains(id))))
        {
            throw new AiToolValidationException("complete", "Provide a nonempty overview and cite or acknowledge all current segment samples in reviewedEvidenceIds.",
                segment.RequiredEvidenceIds.Where(id => !reviewed.Contains(id)));
        }
        job.Result.Topics = merged.Values.OrderBy(topic => topic.StartMs).ThenBy(topic => topic.Id, StringComparer.Ordinal).ToList();
        segment.TopicIds = segmentIds.ToList();
        segment.Overview = overview;
        if (completed)
        {
            segment.State = segmentIds.Count > 0 ? "Completed" : "ReviewedNoTopics";
        }
        job.Result.IsComplete = false;
        AiAnalysisContexts.RefreshOverview(job);
    }

    private static T Value<T>(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<T>(out var value) ? value : throw new InvalidDataException();

    private static T Required<T>(JsonObject item, string key) => item[key] is JsonValue scalar && scalar.TryGetValue<T>(out var value)
        ? value : throw new AiToolValidationException(key, "Required value has the wrong type or is missing.");

    private static string Text(JsonObject item, string key, int maximum)
    {
        var text = Required<string>(item, key);
        return text.Length <= maximum ? text : throw new AiToolValidationException(key, "Maximum length: " + maximum + " characters.");
    }

    private static void ValidateRange(double from, double to, double duration)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to <= from || to > duration)
        {
            throw new AiToolValidationException("timeRange", "Use finite half-open bounds 0 <= start < end <= recording duration.");
        }
    }
}
