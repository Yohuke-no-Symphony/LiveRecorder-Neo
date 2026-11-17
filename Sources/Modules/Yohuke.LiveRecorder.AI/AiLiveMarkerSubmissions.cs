using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

internal static class AiLiveMarkerSubmissions
{
    internal static void Submit(JsonObject args, AiAnalysisJob job, IReadOnlyDictionary<string, AiEvidence> evidence)
    {
        var segment = job.Context.Segments[job.Context.CurrentSegmentIndex];
        var overview = Text(args, "overview", 2000);
        var complete = Value<bool>(args["complete"]);
        var incoming = args["markers"] as JsonArray ?? throw new InvalidDataException();
        if (incoming.Count > 12)
        {
            throw new InvalidDataException();
        }
        var merged = job.Result.LiveMarkers.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var segmentIds = new HashSet<string>(segment.LiveMarkerIds, StringComparer.Ordinal);
        foreach (var value in incoming)
        {
            var item = value as JsonObject ?? throw new InvalidDataException();
            var localId = Text(item, "id", 80);
            var prefix = "s" + segment.Index + ":";
            if (localId.StartsWith(prefix, StringComparison.Ordinal))
            {
                localId = localId[prefix.Length..];
            }
            if (!Regex.IsMatch(localId, "^[a-zA-Z0-9_-]{1,64}$"))
            {
                throw new AiToolValidationException("markers[].id", "Use 1 to 64 ASCII letters, digits, underscores or hyphens, optionally prefixed by this segment's sN:.");
            }
            var anchorId = Text(item, "anchorEvidenceId", 160);
            var message = Text(item, "message", 160).Trim();
            if (string.IsNullOrEmpty(message) || !evidence.TryGetValue(anchorId, out var anchor) ||
                anchor.OffsetMs < segment.StartMs || anchor.OffsetMs >= segment.EndMs)
            {
                throw new AiToolValidationException("markers[].anchorEvidenceId", "Supply a received evidence ID inside the current segment and a nonempty message.");
            }
            var ids = item["evidenceIds"] as JsonArray ?? throw new InvalidDataException();
            if (ids.Count is < 1 or > 8)
            {
                throw new InvalidDataException();
            }
            var draft = new AiLiveMarkerDraft
            {
                Id = prefix + localId, Message = message, AnchorEvidenceId = anchorId, OffsetMs = anchor.OffsetMs
            };
            foreach (var id in ids.Select(Value<string>).Distinct(StringComparer.Ordinal))
            {
                if (!evidence.TryGetValue(id, out var supplied) || Math.Abs(supplied.OffsetMs - anchor.OffsetMs) > 300000)
                {
                    throw new AiToolValidationException("markers[].evidenceIds", "Every ID must have been supplied and lie within five minutes of the anchor.");
                }
                draft.Evidence.Add(supplied);
            }
            if (draft.Evidence.All(entry => entry.Id != anchorId))
            {
                throw new AiToolValidationException("markers[].evidenceIds", "Include anchorEvidenceId in evidenceIds.");
            }
            var duplicate = merged.Values.FirstOrDefault(existing => existing.AnchorEvidenceId == anchorId && existing.Message == message);
            if (duplicate is not null)
            {
                if (draft.Id != duplicate.Id)
                {
                    merged.Remove(draft.Id);
                    segmentIds.Remove(draft.Id);
                }
                draft.Id = duplicate.Id;
            }
            merged[draft.Id] = draft;
            segmentIds.Add(draft.Id);
        }
        if (merged.Count > LiveMarkerLimits.MAXIMUM_ENTRIES)
        {
            throw new AiToolValidationException("markers", "A native live-record file permits at most 20000 entries. Revise redundant entries before adding more.");
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
        if (complete && (string.IsNullOrWhiteSpace(overview) || segment.RequiredEvidenceIds.Count == 0 ||
                         segment.RequiredEvidenceIds.Any(id => !reviewed.Contains(id))))
        {
            throw new AiToolValidationException("complete", "Provide an overview and cite or acknowledge every required current-segment sample.",
                segment.RequiredEvidenceIds.Where(id => !reviewed.Contains(id)));
        }
        job.Result.LiveMarkers = merged.Values.OrderBy(item => item.OffsetMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        segment.LiveMarkerIds = segmentIds.ToList();
        segment.Overview = overview;
        if (complete)
        {
            segment.State = segmentIds.Count > 0 ? "Completed" : "ReviewedNoTopics";
        }
        job.Result.IsComplete = false;
        AiAnalysisContexts.RefreshOverview(job);
    }

    private static T Value<T>(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<T>(out var value) ? value : throw new InvalidDataException();

    private static string Text(JsonObject item, string key, int maximum)
    {
        var text = Value<string>(item[key]);
        return text.Length <= maximum ? text : throw new InvalidDataException();
    }
}
