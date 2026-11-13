using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal static class TimelineProjectionAccumulator
{
    internal const int HEAT_RESOLUTION_MS = 1000;
    private const int MAX_USER_NAME_LENGTH = 128;
    private const int MAX_TEXT_LENGTH = 280;

    internal static void Add(TimelineSourceState state, EventArchiveSource source, RecordedEventEnvelope item, long sequencePrefix)
    {
        if (source.ToMs is { } until && item.OffsetMs >= until)
        {
            return;
        }
        var included = item.OffsetMs >= source.FromMs && item.OffsetMs + source.StartOffsetMs >= 0;
        var offset = item.OffsetMs + source.StartOffsetMs;
        if (item.Kind == "Danmu")
        {
            if (included)
            {
                var bucket = (long)Math.Floor(offset / HEAT_RESOLUTION_MS);
                state.Heat[bucket] = AddQuantity(state.Heat.GetValueOrDefault(bucket), 1);
            }
            return;
        }
        if (item.Kind is not ("Gift" or "SuperChat" or "Guard" or "RedPocket"))
        {
            return;
        }
        var identity = Identity(item);
        if (!state.Markers.TryGetValue(identity, out var marker))
        {
            marker = new() { Kind = item.Kind, Baseline = !included };
            state.Markers.Add(identity, marker);
        }
        if (item.Kind == "Gift")
        {
            AddGift(marker, item, offset, sequencePrefix, included);
            return;
        }
        if (!included || marker.Baseline)
        {
            return;
        }
        Include(marker, item, offset, sequencePrefix);
        marker.Quantity = Math.Max(marker.Quantity, Math.Max(1, item.Quantity));
        if (item.Kind != "RedPocket" && item.PriceSource == "ProtocolPaid" && item.Amount is >= 0)
        {
            marker.Amount = Math.Max(marker.Amount ?? 0, item.Amount.Value);
        }
    }

    private static void AddGift(TimelineMarkerState marker, RecordedEventEnvelope item, double offset, long sequencePrefix, bool included)
    {
        var quantity = Math.Max(0, item.Quantity);
        var row = Key(item.EventId.Length > 0 ? item.EventId : item.Sequence.ToString(CultureInfo.InvariantCulture));
        var firstRow = marker.Rows.Add(row);
        var cumulative = Count(item.Payload, "batchComboCount");
        var combo = Text(item.Payload, "batchComboId").Length > 0;
        var target = combo
            ? cumulative > 0
                ? Math.Max(marker.OriginalQuantity, marker.OriginalQuantity == 0 ? Math.Max(quantity, cumulative) : cumulative)
                : AddQuantity(marker.OriginalQuantity, firstRow ? quantity : 0)
            : Math.Max(marker.OriginalQuantity, quantity);
        var delta = target - marker.OriginalQuantity;
        marker.OriginalQuantity = target;
        if (included && delta > 0)
        {
            Include(marker, item, offset, sequencePrefix);
            marker.Quantity = AddQuantity(marker.Quantity, delta);
        }
        if (included && marker.Included && item.CoinType?.Equals("gold", StringComparison.OrdinalIgnoreCase) == true && item.PriceSource == "ProtocolGold")
        {
            var rawPrice = Text(item.Payload, "unitPrice");
            if (decimal.TryParse(rawPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var unitPrice) && unitPrice >= 0)
            {
                marker.UnitAmount = unitPrice / 1000m;
            }
            else if (item.Amount is >= 0 && quantity > 0)
            {
                marker.UnitAmount = item.Amount.Value / quantity;
            }
        }
        if (marker.Included && marker.UnitAmount is { } price && (marker.Quantity == 0 || price <= decimal.MaxValue / marker.Quantity))
        {
            marker.Amount = price * marker.Quantity;
        }
    }

    private static void Include(TimelineMarkerState marker, RecordedEventEnvelope item, double offset, long sequencePrefix)
    {
        if (!marker.Included || offset < marker.OffsetMs)
        {
            marker.OffsetMs = offset;
            marker.Sequence = checked(sequencePrefix + item.Sequence);
        }
        marker.EndOffsetMs = marker.Included ? Math.Max(marker.EndOffsetMs, offset) : offset;
        marker.Included = true;
        if (!string.IsNullOrWhiteSpace(item.UserName))
        {
            marker.UserName = Preview(item.UserName, MAX_USER_NAME_LENGTH);
        }
        var text = item.Kind == "Gift" ? Text(item.Payload, "giftName") : item.Text;
        text = string.IsNullOrWhiteSpace(text) ? item.Text : text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            marker.Text = Preview(text, MAX_TEXT_LENGTH);
        }
    }

    private static string Identity(RecordedEventEnvelope item)
    {
        var room = Text(item.Payload, "roomId");
        if (item.Kind == "Gift")
        {
            var gift = Text(item.Payload, "giftId");
            gift = gift.Length > 0 ? gift : Text(item.Payload, "giftName");
            var combo = Text(item.Payload, "batchComboId");
            var identity = combo.Length > 0 ? Key("combo", combo)
                : !string.IsNullOrWhiteSpace(item.TransactionId) ? Key("transaction", item.TransactionId)
                : item.EventId.Length > 0 ? Key("event", item.EventId)
                : Key("sequence", item.Sequence.ToString(CultureInfo.InvariantCulture));
            return Key(item.Kind, room, item.UserId, gift, item.CoinType?.ToLowerInvariant(), identity);
        }
        if (item.Kind == "RedPocket")
        {
            var lot = item.TransactionId;
            if (string.IsNullOrWhiteSpace(lot))
            {
                lot = Text(item.Payload?["redPocket"] as JsonObject, "lotId");
            }
            if (!string.IsNullOrWhiteSpace(lot) && lot != "0")
            {
                return Key(item.Kind, room, lot);
            }
        }
        var id = Text(item.Payload, "identity");
        id = id.Length > 0 ? id : Text(item.Payload, "messageId");
        id = id.Length > 0 ? id : item.EventId;
        id = id.Length > 0 ? id : item.TransactionId;
        return Key(item.Kind, room, string.IsNullOrWhiteSpace(id) ? item.Sequence.ToString(CultureInfo.InvariantCulture) : id);
    }

    internal static long AddQuantity(long current, long addition) => addition > long.MaxValue - current ? long.MaxValue : current + addition;
    private static string Key(params string?[] parts) => JsonSerializer.Serialize(parts);
    private static string Text(JsonObject? payload, string name) => payload?[name]?.ToString() ?? "";
    private static string Preview(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..maximumLength];
    private static long Count(JsonObject? payload, string name) => long.TryParse(Text(payload, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : 0;
}
