using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal static class AnalyticsContributionEvent
{
    internal static RecordedEventEnvelope Merge(RecordedEventEnvelope? previous, RecordedEventEnvelope current)
    {
        var result = previous ?? Copy(current);
        if (current.OffsetMs < result.OffsetMs)
        {
            result.OffsetMs = current.OffsetMs;
            result.Sequence = current.Sequence;
            result.ReceivedAtUtc = current.ReceivedAtUtc;
            result.SourceTimeUtc = current.SourceTimeUtc;
        }
        result.UserId = ValidUser(current.UserId) ? current.UserId : result.UserId;
        result.UserName = string.IsNullOrWhiteSpace(current.UserName) ? result.UserName : current.UserName;
        result.AvatarUrl = string.IsNullOrWhiteSpace(current.AvatarUrl) ? result.AvatarUrl : current.AvatarUrl;
        result.Text = string.IsNullOrWhiteSpace(current.Text) ? result.Text : current.Text;
        result.Payload ??= new();
        MergeObject(result.Payload, current.Payload);
        if (current.Kind == "RedPocket" && result.Payload["redPocket"] is JsonObject pocket && !Flag(pocket, "hasResult"))
        {
            pocket["winners"] = new JsonArray();
        }
        return result;
    }

    internal static RecordedEventEnvelope Copy(RecordedEventEnvelope item) => new()
    {
        SchemaVersion = item.SchemaVersion,
        Sequence = item.Sequence,
        EventId = item.EventId,
        Kind = item.Kind,
        Command = item.Command,
        ReceivedAtUtc = item.ReceivedAtUtc,
        SourceTimeUtc = item.SourceTimeUtc,
        OffsetMs = item.OffsetMs,
        UserId = item.UserId,
        UserName = item.UserName,
        Text = item.Text,
        AvatarUrl = item.AvatarUrl,
        Amount = item.Amount,
        Quantity = item.Quantity,
        TransactionId = item.TransactionId,
        CoinType = item.CoinType,
        PriceSource = item.PriceSource,
        Payload = item.Payload?.DeepClone() as JsonObject
    };

    internal static string Text(JsonObject? payload, string name) => payload?[name]?.ToString() ?? "";
    internal static bool ValidUser(string value) => !string.IsNullOrWhiteSpace(value) && value != "0";
    internal static decimal? Number(JsonObject? payload, string name) =>
        decimal.TryParse(Text(payload, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private static void MergeObject(JsonObject target, JsonObject? incoming)
    {
        if (incoming is null)
        {
            return;
        }
        foreach (var pair in incoming)
        {
            if (pair.Value is null || pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (pair.Key is "lotId" or "senderUid" or "startTime" or "endTime" or "currentTime" && pair.Value.ToString() == "0")
            {
                continue;
            }
            if (pair.Key is "hasDetails" or "hasResult" or "isDrawn")
            {
                target[pair.Key] = Flag(target, pair.Key) || Flag(incoming, pair.Key);
            }
            else if (pair.Value is JsonObject child)
            {
                if (target[pair.Key] is not JsonObject targetChild)
                {
                    targetChild = new();
                    target[pair.Key] = targetChild;
                }
                MergeObject(targetChild, child);
            }
            else if (pair.Key is "winners" or "awards" && pair.Value is JsonArray array)
            {
                if (pair.Key != "winners" || Flag(incoming, "hasResult"))
                {
                    target[pair.Key] = MergeArray(target[pair.Key] as JsonArray, array, pair.Key == "winners");
                }
            }
            else
            {
                target[pair.Key] = pair.Value.DeepClone();
            }
        }
    }

    private static JsonArray MergeArray(JsonArray? previous, JsonArray current, bool winners)
    {
        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in (previous ?? []).Concat(current).OfType<JsonObject>())
        {
            var identity = winners
                ? JsonSerializer.Serialize(new[] { Text(item, "uid"), Text(item, "giftId"), Text(item, "userName") })
                : Text(item, "giftId") is { Length: > 0 } giftId ? "id:" + giftId : "name:" + Text(item, "name");
            if (!merged.TryGetValue(identity, out var result))
            {
                result = new();
                merged.Add(identity, result);
            }
            MergeObject(result, item);
        }
        return new(merged.Values.Select(item => (JsonNode)item).ToArray());
    }

    private static bool Flag(JsonObject source, string name) => source[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
}
