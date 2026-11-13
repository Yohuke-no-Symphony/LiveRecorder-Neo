using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>按协议行身份、连击数量差值及报价来源计算可重放的观测流水。</summary>
public sealed class AnalyticsAccumulator
{
    private readonly Dictionary<string, AnalyticsInteractionObservation> interactions = new(StringComparer.Ordinal);
    private readonly HashSet<string> events = new(StringComparer.Ordinal);
    private readonly HashSet<string> giftRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> baselineRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsGiftCombo> combos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsMoneyObservation> money = new(StringComparer.Ordinal);
    private readonly HashSet<string> baselineRedPockets = new(StringComparer.Ordinal);
    private int coverageGaps;
    private long anonymous;

    /// <summary>累计去重后的互动并独立标记同传弹幕；相同身份的价格补齐仍可更新金额。</summary>
    public void Add(RecordedEventEnvelope item, bool isTranslation = false)
    {
        if (!double.IsFinite(item.OffsetMs))
        {
            return;
        }
        if (item.Kind is "Gap" or "Error")
        {
            var key = InteractionKey(item);
            if (key.Length == 0 || events.Add(key))
            {
                coverageGaps++;
            }
            return;
        }
        if (item.Kind is not ("Danmu" or "Gift" or "SuperChat" or "Guard" or "RedPocket"))
        {
            return;
        }
        CountInteraction(item, isTranslation && item.Kind == "Danmu");
        switch (item.Kind)
        {
            case "Gift":
                AddGift(item, false);
                break;
            case "Guard":
            case "SuperChat":
                AddPayment(item, false);
                break;
        }
    }

    /// <summary>建立投影范围之前的连击及支付去重基线，不给当前统计增加金额或互动。</summary>
    public void SeedBaseline(RecordedEventEnvelope item)
    {
        if (!double.IsFinite(item.OffsetMs))
        {
            return;
        }
        if (item.Kind == "Gift")
        {
            AddGift(item, true);
        }
        else if (item.Kind is "Guard" or "SuperChat")
        {
            AddPayment(item, true);
        }
        else if (item.Kind == "RedPocket" && InteractionKey(item) is { Length: > 0 } key)
        {
            baselineRedPockets.Add(key);
        }
    }

    private void CountInteraction(RecordedEventEnvelope item, bool isTranslation)
    {
        var key = InteractionKey(item);
        if (item.Kind == "RedPocket" && baselineRedPockets.Contains(key))
        {
            return;
        }
        key = key.Length > 0 ? key : Anonymous();
        if (!interactions.TryGetValue(key, out var interaction))
        {
            interaction = new() { Kind = item.Kind, BucketKey = Bucket(item) };
            interactions.Add(key, interaction);
        }
        interaction.IsTranslation |= isTranslation;
        if (ValidUser(item.UserId))
        {
            interaction.UserId = item.UserId;
        }
        if (!string.IsNullOrWhiteSpace(item.UserName))
        {
            interaction.UserName = item.UserName;
        }
        if (item.Kind == "RedPocket" && item.Amount is >= 0 && item.PriceSource == "ProtocolPaid")
        {
            interaction.RedPocketAmount = Math.Max(interaction.RedPocketAmount ?? 0, item.Amount.Value);
        }
    }

    private void AddGift(RecordedEventEnvelope item, bool baseline)
    {
        var giftId = Text(item.Payload, "giftId");
        var giftName = Text(item.Payload, "giftName");
        var gift = giftId.Length > 0 ? giftId : giftName;
        var coin = item.CoinType?.Trim().ToLowerInvariant() ?? "";
        var comboId = Text(item.Payload, "batchComboId");
        var quantity = ReadCount(item.Payload, "quantity");
        quantity = quantity > 0 ? quantity : Math.Max(0, item.Quantity);
        var cumulative = ReadCount(item.Payload, "batchComboCount");
        var identity = GiftIdentity(item);
        var identified = identity.Length > 0 || comboId.Length > 0 && cumulative > 0;
        var key = identity.Length > 0 ? identity : Anonymous();
        var firstRow = giftRows.Add(key);
        money.TryGetValue(key, out var entry);
        long delta;
        if (comboId.Length > 0)
        {
            var comboKey = Key("combo", Room(item), item.UserId, gift, coin, comboId);
            if (!combos.TryGetValue(comboKey, out var combo))
            {
                combo = new();
                combos.Add(comboKey, combo);
            }
            var target = cumulative > 0 ? Math.Max(combo.Quantity, combo.Quantity == 0 ? Math.Max(quantity, cumulative) : cumulative)
                : checked(combo.Quantity + (firstRow ? quantity : 0));
            delta = target - combo.Quantity;
            combo.Quantity = target;
        }
        else
        {
            delta = Math.Max(0, quantity - (entry?.GiftQuantity ?? (baselineRows.Contains(key) ? quantity : 0)));
        }
        if (baseline)
        {
            if (entry is null)
            {
                baselineRows.Add(key);
            }
            return;
        }
        if (entry is null && delta == 0)
        {
            return;
        }
        var category = coin == "gold" ? AnalyticsMoneyCategory.GoldGift : coin == "silver" ? AnalyticsMoneyCategory.NonMonetary : AnalyticsMoneyCategory.Unknown;
        if (entry is null)
        {
            entry = CreateObservation(item, category, !identified, key);
            money.Add(key, entry);
        }
        EnrichUser(entry, item);
        entry.GiftQuantity = checked(entry.GiftQuantity + delta);
        if (category == AnalyticsMoneyCategory.NonMonetary)
        {
            return;
        }
        var unitPrice = GiftUnitValue(item, quantity);
        if (category != AnalyticsMoneyCategory.GoldGift || unitPrice is null)
        {
            entry.UnknownGiftQuantity = checked(entry.UnknownGiftQuantity + delta);
            entry.UnknownAmount = entry.UnknownGiftQuantity > 0;
            return;
        }
        entry.Amount += unitPrice.Value * delta;
        if (entry.UnknownGiftQuantity > 0)
        {
            entry.Amount += unitPrice.Value * entry.UnknownGiftQuantity;
            entry.UnknownGiftQuantity = 0;
        }
        entry.UnknownAmount = false;
        if (comboId.Length == 0 && item.Amount is >= 0)
        {
            entry.Amount = Math.Max(entry.Amount, item.Amount.Value);
        }
    }

    private void AddPayment(RecordedEventEnvelope item, bool baseline)
    {
        var command = item.Command.Split(':', 2)[0];
        var quote = item.Kind == "Guard" && (item.PriceSource == "ProtocolQuote" || command == "GUARD_BUY");
        var paid = item.Kind == "SuperChat" || item.PriceSource == "ProtocolPaid" || command.StartsWith("USER_TOAST_MSG", StringComparison.Ordinal);
        var category = quote ? AnalyticsMoneyCategory.Quote : paid ? AnalyticsMoneyCategory.ProtocolPaid : AnalyticsMoneyCategory.Unknown;
        var identity = PaymentIdentity(item);
        var identified = identity.Length > 0;
        identity = identified ? identity : Anonymous();
        var key = Key(item.Kind, category.ToString(), identity);
        if (baseline)
        {
            if (!money.ContainsKey(key))
            {
                baselineRows.Add(key);
            }
            return;
        }
        if (baselineRows.Contains(key))
        {
            return;
        }
        if (!money.TryGetValue(key, out var entry))
        {
            entry = CreateObservation(item, category, !identified, identity);
            money.Add(key, entry);
            entry.UnknownAmount = true;
        }
        EnrichUser(entry, item);
        var quantity = ReadCount(item.Payload, "quantity");
        entry.Quantity = Math.Max(entry.Quantity, quantity > 0 ? quantity : Math.Max(1, item.Quantity));
        if (item.Amount is >= 0 && item.PriceSource != "Unknown" && category != AnalyticsMoneyCategory.Unknown)
        {
            entry.Amount = Math.Max(entry.Amount, item.Amount.Value);
            entry.UnknownAmount = false;
        }
    }

    /// <summary>生成独立快照，将协议报价、金币面值、支付观测和未知价格分别列示。</summary>
    public AnalyticsReport Build()
    {
        return new AnalyticsReportBuilder().Build(interactions.Values, money.Values, coverageGaps);
    }

    private static AnalyticsMoneyObservation CreateObservation(RecordedEventEnvelope item, AnalyticsMoneyCategory category, bool unidentified, string identity) => new()
    {
        Kind = item.Kind, Identity = identity, Category = category, UserId = item.UserId, UserName = item.UserName,
        BucketKey = Bucket(item), Unidentified = unidentified
    };

    private static string InteractionKey(RecordedEventEnvelope item)
    {
        if (item.Kind == "Gift")
        {
            return GiftIdentity(item);
        }
        if (item.Kind is "Guard" or "SuperChat")
        {
            var identity = PaymentIdentity(item);
            return identity.Length > 0 ? Key(item.Kind, identity) : "";
        }
        if (item.Kind == "RedPocket")
        {
            var lotId = !string.IsNullOrWhiteSpace(item.TransactionId) ? item.TransactionId : Text(item.Payload?["redPocket"] as JsonObject, "lotId");
            if (!string.IsNullOrWhiteSpace(lotId) && lotId != "0")
            {
                return Key("pocket", Room(item), lotId);
            }
        }
        if (item.EventId.Length == 0)
        {
            return "";
        }
        return Key(item.Kind, item.EventId);
    }

    private static string GiftIdentity(RecordedEventEnvelope item)
    {
        var nativeId = item.EventId.Length > 0 ? item.EventId : Text(item.Payload, "messageId");
        var transaction = !string.IsNullOrWhiteSpace(item.TransactionId) ? item.TransactionId : Text(item.Payload, "transactionId");
        var giftId = Text(item.Payload, "giftId");
        var gift = giftId.Length > 0 ? giftId : Text(item.Payload, "giftName");
        var coin = item.CoinType?.Trim().ToLowerInvariant() ?? "";
        if (nativeId.Length > 0)
        {
            return Key("gift-row", Room(item), item.UserId, gift, coin, nativeId);
        }
        return !string.IsNullOrWhiteSpace(transaction) ? Key("gift-transaction", Room(item), item.UserId, gift, coin, transaction) : "";
    }

    private static string PaymentIdentity(RecordedEventEnvelope item)
    {
        var serverId = Text(item.Payload, "messageId");
        if (serverId.Length > 0)
        {
            return Key("server", Room(item), serverId);
        }
        var identity = Text(item.Payload, "identity");
        if (identity.Length > 0)
        {
            return identity;
        }
        if (item.EventId.Length > 0)
        {
            return item.EventId;
        }
        return !string.IsNullOrWhiteSpace(item.TransactionId) ? Key("transaction", Room(item), item.TransactionId) : "";
    }

    private static void EnrichUser(AnalyticsMoneyObservation entry, RecordedEventEnvelope item)
    {
        if (ValidUser(item.UserId))
        {
            entry.UserId = item.UserId;
        }
        if (!string.IsNullOrWhiteSpace(item.UserName))
        {
            entry.UserName = item.UserName;
        }
    }

    private string Anonymous() => Key("unidentified", checked(++anonymous).ToString(CultureInfo.InvariantCulture));
    private static string Room(RecordedEventEnvelope item) => Text(item.Payload, "roomId");
    private static bool ValidUser(string value) => !string.IsNullOrWhiteSpace(value) && value != "0";
    private static long Bucket(RecordedEventEnvelope item) => (long)Math.Max(0, item.OffsetMs / 60000);
    private static string Key(params string?[] parts) => JsonSerializer.Serialize(parts);
    private static string Text(JsonObject? payload, string key) => payload?[key]?.ToString() ?? "";

    private static long ReadCount(JsonObject? payload, string name)
    {
        return long.TryParse(Text(payload, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : 0;
    }

    private static decimal? GiftUnitValue(RecordedEventEnvelope item, long quantity)
    {
        if (item.Amount is < 0 || item.PriceSource is "Unknown" or "NonMonetary")
        {
            return null;
        }
        if (item.Payload?.ContainsKey("unitPrice") == true && decimal.TryParse(Text(item.Payload, "unitPrice"), NumberStyles.Number, CultureInfo.InvariantCulture, out var raw) && raw >= 0)
        {
            return raw / 1000m;
        }
        return item.Amount is >= 0 && quantity > 0 ? item.Amount.Value / quantity : null;
    }
}
