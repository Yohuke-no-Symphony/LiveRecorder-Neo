using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal static class PlaybackEventIdentity
{
    internal static bool IncludesKind(string? requestedKind, string eventKind)
    {
        return eventKind is "Danmu" or "SuperChat" or "Gift" or "Guard" or "RedPocket" or "Gap" &&
               (string.IsNullOrEmpty(requestedKind) || string.Equals(requestedKind, "All", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventKind, requestedKind, StringComparison.OrdinalIgnoreCase));
    }

    internal static string Key(RecordedEventEnvelope item)
    {
        return RedPocketKey(item) is { } pocketKey
            ? "RedPocket:" + pocketKey
            : "Event:" + item.Sequence.ToString(CultureInfo.InvariantCulture);
    }

    internal static string? RedPocketKey(RecordedEventEnvelope item)
    {
        if (item.Kind != "RedPocket")
        {
            return null;
        }
        if (!string.IsNullOrEmpty(item.TransactionId))
        {
            return item.TransactionId;
        }
        if (item.Payload?["redPocket"] is not JsonObject pocket || pocket["lotId"] is not JsonValue value)
        {
            return null;
        }
        if (value.GetValueKind() == JsonValueKind.String)
        {
            var text = value.GetValue<string>();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        if (value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        var number = value.Deserialize<double>();
        return number != 0 && double.IsFinite(number) ? NumberKey(number) : null;
    }

    private static string NumberKey(double number)
    {
        var text = number.ToString("G", CultureInfo.InvariantCulture);
        var magnitude = Math.Abs(number);
        if (magnitude >= 1e-6 && magnitude < 1e21)
        {
            return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                .ToString("0.############################", CultureInfo.InvariantCulture);
        }
        var exponentStart = text.IndexOf('E');
        var exponent = int.Parse(text.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return text[..exponentStart] + "e" + exponent.ToString("+0;-0;0", CultureInfo.InvariantCulture);
    }
}
