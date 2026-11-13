namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsReportBuilder
{
    private const int RANKING_LIMIT = 100;
    private readonly AnalyticsReport report = new();
    private readonly Dictionary<string, AnalyticsUser> users = new(StringComparer.Ordinal);
    private readonly Dictionary<long, AnalyticsBucketAccumulator> buckets = new();
    private readonly HashSet<string> unknownRedPocketUsers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsCategorySummary> categories = new(StringComparer.Ordinal)
    {
        ["SuperChat"] = new() { Kind = "SuperChat" },
        ["Gift"] = new() { Kind = "Gift" },
        ["Guard"] = new() { Kind = "Guard" }
    };
    private readonly Dictionary<string, HashSet<string>> categoryUsers = new(StringComparer.Ordinal)
    {
        ["SuperChat"] = new(StringComparer.Ordinal),
        ["Gift"] = new(StringComparer.Ordinal),
        ["Guard"] = new(StringComparer.Ordinal)
    };

    internal AnalyticsReport Build(IReadOnlyCollection<AnalyticsInteractionObservation> interactions, IReadOnlyCollection<AnalyticsMoneyObservation> money, int coverageGaps)
    {
        report.SchemaVersion = AnalyticsReport.CURRENT_SCHEMA_VERSION;
        report.BuiltAtUtc = DateTime.UtcNow;
        report.CoverageGaps = coverageGaps;
        foreach (var interaction in interactions)
        {
            AddInteraction(interaction);
        }
        foreach (var entry in money)
        {
            AddMoney(entry);
        }
        foreach (var purchase in money.Where(entry => entry.Kind == "Guard").GroupBy(entry => entry.Identity, StringComparer.Ordinal))
        {
            var entry = purchase.OrderByDescending(entry => entry.Category == AnalyticsMoneyCategory.ProtocolPaid).ThenByDescending(entry => entry.Quantity).First();
            categories["Guard"].Quantity += entry.Quantity;
            if (User(entry.UserId, entry.UserName) is { } user)
            {
                user.GuardQuantity += entry.Quantity;
            }
        }
        foreach (var userId in unknownRedPocketUsers)
        {
            users[userId].RedPocketAmount = null;
        }
        foreach (var category in categories.Values)
        {
            category.Users = categoryUsers[category.Kind].Count;
        }
        foreach (var bucket in buckets.Values)
        {
            bucket.Value.ActiveUsers = bucket.UserIds.Count;
            bucket.Value.DanmuUsers = bucket.DanmuUserIds.Count;
            bucket.Value.PayingUsers = bucket.PayingUserIds.Count;
        }
        report.ObservedAmount = report.GoldGiftValue + report.ProtocolPaidAmount;
        report.ActiveUsers = users.Count;
        report.DanmuUsers = users.Values.Count(user => user.DanmuCount > 0);
        report.PayingUsers = users.Values.Count(user => user.PaidAmount > 0);
        report.Categories = categories.Values.ToList();
        report.TopUsers = users.Values.OrderByDescending(user => user.Messages).ThenByDescending(user => user.PaidAmount)
            .ThenBy(user => user.UserId, StringComparer.Ordinal).Take(RANKING_LIMIT).ToList();
        report.UserRankings = new(StringComparer.Ordinal)
        {
            ["danmuCount"] = Rank(user => user.DanmuCount),
            ["superChatAmount"] = Rank(user => user.SuperChatAmount),
            ["superChatCount"] = Rank(user => user.SuperChatCount),
            ["giftAmount"] = Rank(user => user.GiftAmount),
            ["giftQuantity"] = Rank(user => user.GiftQuantity),
            ["guardAmount"] = Rank(user => user.GuardAmount),
            ["guardQuantity"] = Rank(user => user.GuardQuantity),
            ["redPocketAmount"] = Rank(user => user.RedPocketAmount),
            ["redPocketCount"] = Rank(user => user.RedPocketCount)
        };
        report.Buckets = buckets.OrderBy(pair => pair.Key).Select(pair => pair.Value.Value).ToList();
        return report;
    }

    private void AddInteraction(AnalyticsInteractionObservation interaction)
    {
        var bucket = Bucket(interaction.BucketKey);
        bucket.Value.Messages++;
        var user = User(interaction.UserId, interaction.UserName);
        if (user is not null)
        {
            user.Messages++;
            bucket.UserIds.Add(user.UserId);
            if (categoryUsers.TryGetValue(interaction.Kind, out var participants))
            {
                participants.Add(user.UserId);
            }
        }
        if (categories.TryGetValue(interaction.Kind, out var category))
        {
            category.Count++;
        }
        switch (interaction.Kind)
        {
            case "Danmu":
                report.DanmuCount++;
                bucket.Value.DanmuCount++;
                if (user is not null)
                {
                    user.DanmuCount++;
                    bucket.DanmuUserIds.Add(user.UserId);
                }
                break;
            case "Gift":
                report.GiftCount++;
                break;
            case "SuperChat":
                report.SuperChatCount++;
                categories["SuperChat"].Quantity++;
                if (user is not null)
                {
                    user.SuperChatCount++;
                }
                break;
            case "Guard":
                report.GuardCount++;
                break;
            case "RedPocket":
                report.RedPocketCount++;
                if (user is not null)
                {
                    user.RedPocketCount++;
                    if (interaction.RedPocketAmount is { } amount)
                    {
                        user.RedPocketAmount = (user.RedPocketAmount ?? 0) + amount;
                    }
                    else
                    {
                        unknownRedPocketUsers.Add(user.UserId);
                    }
                }
                break;
        }
    }

    private void AddMoney(AnalyticsMoneyObservation entry)
    {
        report.GiftQuantity += entry.GiftQuantity;
        report.UnknownGiftQuantity += entry.UnknownGiftQuantity;
        if (entry.UnknownAmount)
        {
            report.UnknownAmountCount++;
        }
        if (entry.Unidentified && entry.Category != AnalyticsMoneyCategory.NonMonetary)
        {
            report.UnidentifiedFinancialEvents++;
        }
        var user = User(entry.UserId, entry.UserName);
        if (entry.Kind == "Gift")
        {
            categories["Gift"].Quantity += entry.GiftQuantity;
            if (user is not null)
            {
                user.GiftQuantity += entry.GiftQuantity;
            }
        }
        switch (entry.Category)
        {
            case AnalyticsMoneyCategory.GoldGift:
                report.GoldGiftValue += entry.Amount;
                break;
            case AnalyticsMoneyCategory.ProtocolPaid:
                report.ProtocolPaidAmount += entry.Amount;
                break;
            case AnalyticsMoneyCategory.Quote:
                report.QuotedAmount += entry.Amount;
                return;
            default:
                return;
        }
        categories[entry.Kind].Amount += entry.Amount;
        var bucket = Bucket(entry.BucketKey);
        bucket.Value.PaidAmount += entry.Amount;
        if (user is not null)
        {
            user.PaidAmount += entry.Amount;
            bucket.UserIds.Add(user.UserId);
            categoryUsers[entry.Kind].Add(user.UserId);
            if (entry.Amount > 0)
            {
                bucket.PayingUserIds.Add(user.UserId);
            }
        }
        switch (entry.Kind)
        {
            case "Gift":
                bucket.Value.GiftAmount += entry.Amount;
                if (user is not null)
                {
                    user.GiftAmount += entry.Amount;
                }
                break;
            case "SuperChat":
                bucket.Value.SuperChatAmount += entry.Amount;
                if (user is not null)
                {
                    user.SuperChatAmount += entry.Amount;
                }
                break;
            case "Guard":
                bucket.Value.GuardAmount += entry.Amount;
                if (user is not null)
                {
                    user.GuardAmount += entry.Amount;
                }
                break;
        }
    }

    private AnalyticsUser? User(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "0")
        {
            return null;
        }
        if (!users.TryGetValue(id, out var user))
        {
            user = new() { UserId = id };
            users.Add(id, user);
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            user.UserName = name;
        }
        return user;
    }

    private AnalyticsBucketAccumulator Bucket(long key)
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new(key);
            buckets.Add(key, bucket);
        }
        return bucket;
    }

    private List<AnalyticsUser> Rank(Func<AnalyticsUser, decimal?> metric) => users.Values.Where(user => metric(user) is > 0)
        .OrderByDescending(metric).ThenByDescending(user => user.PaidAmount).ThenByDescending(user => user.Messages)
        .ThenBy(user => user.UserId, StringComparer.Ordinal).Take(RANKING_LIMIT).ToList();
}
