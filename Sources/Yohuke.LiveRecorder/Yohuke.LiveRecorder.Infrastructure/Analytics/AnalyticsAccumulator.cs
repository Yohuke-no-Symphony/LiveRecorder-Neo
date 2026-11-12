using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>按协议行身份、连击数量差值及报价来源计算可重放的观测流水。</summary>
public sealed class AnalyticsAccumulator
{
    private const string GOLD = "GoldGift";
    private const string PAID = "ProtocolPaid";
    private const string QUOTE = "Quote";
    private const string UNKNOWN = "Unknown";
    private const string NON_MONETARY = "NonMonetary";
    private readonly Dictionary<string, AnalyticsUser> users = new(StringComparer.Ordinal);
    private readonly Dictionary<long, AnalyticsBucket> buckets = new();
    private readonly HashSet<string> events = new(StringComparer.Ordinal);
    private readonly HashSet<string> giftRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> baselineRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsGiftCombo> combos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsMoneyObservation> money = new(StringComparer.Ordinal);
    private readonly HashSet<string> redPockets = new(StringComparer.Ordinal);
    private readonly AnalyticsReport counts = new();
    private long anonymous;

    /// <summary>累计事件互动；相同身份的价格补齐仍可更新金额，而不重复增加互动数。</summary>
    public void Add(RecordedEventEnvelope item)
    {
        if (!double.IsFinite(item.OffsetMs))
        {
            return;
        }
        var interactionKey = InteractionKey(item);
        var newInteraction = interactionKey.Length == 0 || events.Add(interactionKey);
        if (item.Kind is "Gap" or "Error")
        {
            if (newInteraction)
            {
                counts.CoverageGaps++;
            }
            return;
        }
        if (item.Kind is not ("Danmu" or "Gift" or "SuperChat" or "Guard" or "RedPocket"))
        {
            return;
        }
        if (newInteraction)
        {
            CountInteraction(item);
        }
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
        if (item.Kind == "Gift")
        {
            AddGift(item, true);
        }
        else if (item.Kind is "Guard" or "SuperChat")
        {
            AddPayment(item, true);
        }
    }

    private void CountInteraction(RecordedEventEnvelope item)
    {
        var bucketKey = Bucket(item);
        if (!buckets.TryGetValue(bucketKey, out var bucket))
        {
            bucket = new() { OffsetMs = bucketKey * 60000 };
            buckets.Add(bucketKey, bucket);
        }
        bucket.Messages++;
        if (ValidUser(item.UserId))
        {
            if (!users.TryGetValue(item.UserId, out var user))
            {
                user = new() { UserId = item.UserId };
                users.Add(item.UserId, user);
            }
            user.UserName = item.UserName;
            user.Messages++;
        }
        switch (item.Kind)
        {
            case "Danmu":
                counts.DanmuCount++;
                break;
            case "Gift":
                counts.GiftCount++;
                break;
            case "SuperChat":
                counts.SuperChatCount++;
                break;
            case "Guard":
                counts.GuardCount++;
                break;
            case "RedPocket":
                var identity = !string.IsNullOrWhiteSpace(item.TransactionId) ? Key("pocket", Room(item), item.TransactionId)
                    : item.EventId.Length > 0 ? Key("pocket-event", item.EventId) : Anonymous();
                if (redPockets.Add(identity))
                {
                    counts.RedPocketCount++;
                }
                break;
        }
    }

    private void AddGift(RecordedEventEnvelope item, bool baseline)
    {
        var giftId = Text(item.Payload, "giftId");
        var giftName = Text(item.Payload, "giftName");
        var gift = giftId.Length > 0 ? giftId : giftName;
        var coin = item.CoinType?.Trim().ToLowerInvariant() ?? "";
        var nativeId = item.EventId.Length > 0 ? item.EventId : Text(item.Payload, "messageId");
        var transaction = !string.IsNullOrWhiteSpace(item.TransactionId) ? item.TransactionId : Text(item.Payload, "transactionId");
        var comboId = Text(item.Payload, "batchComboId");
        var quantity = ReadCount(item.Payload, "quantity");
        quantity = quantity > 0 ? quantity : Math.Max(0, item.Quantity);
        var cumulative = ReadCount(item.Payload, "batchComboCount");
        var identified = nativeId.Length > 0 || !string.IsNullOrWhiteSpace(transaction) || comboId.Length > 0 && cumulative > 0;
        var key = nativeId.Length > 0 ? Key("gift-row", Room(item), item.UserId, gift, coin, nativeId)
            : !string.IsNullOrWhiteSpace(transaction) ? Key("gift-transaction", Room(item), item.UserId, gift, coin, transaction)
            : Anonymous();
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
        var category = coin == "gold" ? GOLD : coin == "silver" ? NON_MONETARY : UNKNOWN;
        if (entry is null)
        {
            entry = CreateObservation(item, category, !identified);
            money.Add(key, entry);
        }
        entry.GiftQuantity = checked(entry.GiftQuantity + delta);
        if (category == NON_MONETARY)
        {
            return;
        }
        var unitPrice = GiftUnitValue(item, quantity);
        if (category != GOLD || unitPrice is null)
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
        var category = quote ? QUOTE : paid ? PAID : UNKNOWN;
        var serverId = Text(item.Payload, "messageId");
        var identity = serverId.Length > 0 ? Key("server", Room(item), serverId)
            : Text(item.Payload, "identity");
        if (identity.Length == 0)
        {
            identity = item.EventId;
        }
        var identified = identity.Length > 0 || !string.IsNullOrWhiteSpace(item.TransactionId);
        if (identity.Length == 0)
        {
            identity = !string.IsNullOrWhiteSpace(item.TransactionId) ? Key("transaction", Room(item), item.TransactionId) : Anonymous();
        }
        var key = Key(item.Kind, category, identity);
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
            entry = CreateObservation(item, category, !identified);
            money.Add(key, entry);
            entry.UnknownAmount = true;
        }
        if (item.Amount is >= 0 && item.PriceSource != "Unknown" && category != UNKNOWN)
        {
            entry.Amount = Math.Max(entry.Amount, item.Amount.Value);
            entry.UnknownAmount = false;
        }
    }

    /// <summary>生成独立快照，将协议报价、金币面值、支付观测和未知价格分别列示。</summary>
    public AnalyticsReport Build()
    {
        var result = new AnalyticsReport
        {
            SchemaVersion = AnalyticsReport.CURRENT_SCHEMA_VERSION,
            BuiltAtUtc = DateTime.UtcNow,
            DanmuCount = counts.DanmuCount,
            GiftCount = counts.GiftCount,
            GuardCount = counts.GuardCount,
            SuperChatCount = counts.SuperChatCount,
            RedPocketCount = counts.RedPocketCount,
            CoverageGaps = counts.CoverageGaps,
            GoldGiftValue = money.Values.Where(entry => entry.Category == GOLD).Sum(entry => entry.Amount),
            ProtocolPaidAmount = money.Values.Where(entry => entry.Category == PAID).Sum(entry => entry.Amount),
            QuotedAmount = money.Values.Where(entry => entry.Category == QUOTE).Sum(entry => entry.Amount),
            UnknownAmountCount = money.Values.Count(entry => entry.UnknownAmount),
            UnknownGiftQuantity = money.Values.Sum(entry => entry.UnknownGiftQuantity),
            GiftQuantity = money.Values.Sum(entry => entry.GiftQuantity),
            UnidentifiedFinancialEvents = money.Values.Count(entry => entry.Unidentified && entry.Category != NON_MONETARY)
        };
        result.ObservedAmount = result.GoldGiftValue + result.ProtocolPaidAmount;
        var userRows = users.ToDictionary(pair => pair.Key, pair => new AnalyticsUser
        {
            UserId = pair.Value.UserId, UserName = pair.Value.UserName, Messages = pair.Value.Messages
        }, StringComparer.Ordinal);
        var bucketRows = buckets.ToDictionary(pair => pair.Key, pair => new AnalyticsBucket { OffsetMs = pair.Value.OffsetMs, Messages = pair.Value.Messages });
        foreach (var entry in money.Values.Where(entry => entry.Category is GOLD or PAID))
        {
            if (ValidUser(entry.UserId))
            {
                if (!userRows.TryGetValue(entry.UserId, out var user))
                {
                    user = new() { UserId = entry.UserId, UserName = entry.UserName };
                    userRows.Add(entry.UserId, user);
                }
                user.PaidAmount += entry.Amount;
            }
            if (!bucketRows.TryGetValue(entry.BucketKey, out var bucket))
            {
                bucket = new() { OffsetMs = entry.BucketKey * 60000 };
                bucketRows.Add(entry.BucketKey, bucket);
            }
            bucket.PaidAmount += entry.Amount;
        }
        result.ActiveUsers = userRows.Count;
        result.TopUsers = userRows.Values.OrderByDescending(user => user.Messages).ThenByDescending(user => user.PaidAmount).ThenBy(user => user.UserId, StringComparer.Ordinal).Take(100).ToList();
        result.Buckets = bucketRows.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        return result;
    }

    private static AnalyticsMoneyObservation CreateObservation(RecordedEventEnvelope item, string category, bool unidentified) => new()
    {
        Category = category, UserId = item.UserId, UserName = item.UserName, BucketKey = Bucket(item), Unidentified = unidentified
    };

    private static string InteractionKey(RecordedEventEnvelope item)
    {
        if (item.EventId.Length == 0)
        {
            return "";
        }
        return item.Kind == "Gift" ? Key(item.Kind, item.EventId, Text(item.Payload, "giftId"), Text(item.Payload, "giftName"), item.CoinType)
            : Key(item.Kind, item.EventId);
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
