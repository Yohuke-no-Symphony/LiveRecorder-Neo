using System.Globalization;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal static class AnalyticsContributionBuilder
{
    private static IReadOnlyList<string> Kinds { get; } = ["SuperChat", "Gift", "Guard", "RedPocket"];

    internal static AnalyticsContributionSnapshot Build(AnalyticsAccumulator accumulator, string kind)
    {
        var records = accumulator.Money.GroupBy(item => JsonSerializer.Serialize(new[] { item.Kind, item.Identity }), StringComparer.Ordinal)
            .Select(Money).Concat(accumulator.Interactions.Where(item => item.SourceEvent is not null).Select(RedPocket))
            .OrderBy(item => item.OffsetMs).ThenBy(item => item.Sequence).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        var pockets = records.Where(item => item.Kind == "RedPocket").ToList();
        var summary = new AnalyticsContributionSummary
        {
            Count = records.Count,
            Users = records.Where(item => AnalyticsContributionEvent.ValidUser(item.UserId)).Select(item => item.UserId).Distinct(StringComparer.Ordinal).Count(),
            Quantity = records.Sum(item => item.Quantity),
            Amount = records.Sum(item => item.KnownAmount),
            QuotedAmount = records.Sum(item => item.QuotedAmount ?? 0),
            UnknownAmountCount = records.Sum(item => item.UnknownAmountCount),
            TotalBattery = kind is "RedPocket" or "All" && pockets.All(item => item.TotalBattery.HasValue) ? pockets.Sum(item => item.TotalBattery ?? 0) : null
        };
        var users = kind == "All" ? records.GroupBy(UserIdentity, StringComparer.Ordinal).Select(User)
            : records.GroupBy(GroupIdentity, StringComparer.Ordinal).Select(Group);
        return new()
        {
            Events = records,
            Users = users
                .OrderByDescending(item => item.KnownAmount).ThenByDescending(item => item.Quantity)
                .ThenBy(item => item.UserId, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToList(),
            Summary = summary
        };
    }

    private static AnalyticsContribution Money(IGrouping<string, AnalyticsMoneyObservation> group)
    {
        var observations = group.ToList();
        var preferred = observations.OrderByDescending(item => item.Category == AnalyticsMoneyCategory.ProtocolPaid)
            .ThenByDescending(item => item.Category == AnalyticsMoneyCategory.GoldGift)
            .ThenByDescending(item => item.Category == AnalyticsMoneyCategory.Quote)
            .ThenByDescending(item => item.Quantity).First();
        var source = AnalyticsContributionEvent.Copy(preferred.SourceEvent!);
        foreach (var item in observations.Where(item => !ReferenceEquals(item, preferred)))
        {
            AnalyticsContributionEvent.Merge(source, item.SourceEvent!);
        }
        source.UserId = AnalyticsContributionEvent.ValidUser(preferred.UserId) ? preferred.UserId : source.UserId;
        source.UserName = string.IsNullOrWhiteSpace(preferred.UserName) ? source.UserName : preferred.UserName;
        var result = Describe(group.Key, source);
        result.Quantity = source.Kind == "Gift" ? preferred.GiftQuantity : source.Kind == "SuperChat" ? 1 : preferred.Quantity;
        result.KnownAmount = observations.Where(item => item.Category is AnalyticsMoneyCategory.GoldGift or AnalyticsMoneyCategory.ProtocolPaid).Sum(item => item.Amount);
        result.UnknownAmountCount = observations.Count(item => item.UnknownAmount);
        var hasObservedAmount = observations.Any(item => item.Category is AnalyticsMoneyCategory.GoldGift or AnalyticsMoneyCategory.ProtocolPaid or AnalyticsMoneyCategory.NonMonetary);
        result.Amount = hasObservedAmount && !observations.Any(item => item.Category != AnalyticsMoneyCategory.Quote && item.UnknownAmount) ? result.KnownAmount : null;
        var quotes = observations.Where(item => item.Category == AnalyticsMoneyCategory.Quote).ToList();
        result.QuotedAmount = quotes.Count > 0 && quotes.All(item => !item.UnknownAmount) ? quotes.Sum(item => item.Amount) : null;
        result.PriceSource = preferred.UnknownAmount ? "Unknown" : preferred.Category switch
        {
            AnalyticsMoneyCategory.GoldGift => "ProtocolGold",
            AnalyticsMoneyCategory.ProtocolPaid => "ProtocolPaid",
            AnalyticsMoneyCategory.Quote => "ProtocolQuote",
            AnalyticsMoneyCategory.NonMonetary => "NonMonetary",
            _ => "Unknown"
        };
        source.Payload ??= new();
        source.Payload["quantity"] = result.Quantity;
        result.Event = DisplayEvent(source, result.Quantity, result.PriceSource == "ProtocolQuote" ? result.QuotedAmount : result.Amount, result.PriceSource);
        return result;
    }

    private static AnalyticsContribution RedPocket(AnalyticsInteractionObservation interaction)
    {
        var source = AnalyticsContributionEvent.Copy(interaction.SourceEvent!);
        var pocket = source.Payload?["redPocket"] as System.Text.Json.Nodes.JsonObject;
        var lot = source.TransactionId;
        lot = string.IsNullOrWhiteSpace(lot) ? AnalyticsContributionEvent.Text(pocket, "lotId") : lot;
        var id = JsonSerializer.Serialize(new[] { source.Kind, AnalyticsContributionEvent.Text(source.Payload, "roomId"), lot, string.IsNullOrWhiteSpace(lot) || lot == "0" ? source.EventId + ":" + source.Sequence.ToString(CultureInfo.InvariantCulture) : "" });
        var result = Describe(id, source);
        result.Quantity = 1;
        result.Amount = interaction.RedPocketAmount;
        result.TotalBattery = AnalyticsContributionEvent.Number(pocket, "totalBattery");
        result.PriceSource = result.Amount.HasValue ? "ProtocolPaid" : "Unknown";
        result.Event = DisplayEvent(source, 1, result.Amount, result.PriceSource);
        return result;
    }

    private static AnalyticsContribution Describe(string identity, RecordedEventEnvelope source) => new()
    {
        Id = identity,
        Kind = source.Kind,
        UserId = source.UserId,
        UserName = source.UserName,
        AvatarUrl = source.AvatarUrl,
        OffsetMs = source.OffsetMs,
        Sequence = source.Sequence,
        Text = source.Text,
        GiftId = AnalyticsContributionEvent.Text(source.Payload, "giftId"),
        GiftName = AnalyticsContributionEvent.Text(source.Payload, "giftName"),
        GuardLevel = int.TryParse(AnalyticsContributionEvent.Text(source.Payload, "guardLevel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : null,
        CoinType = source.CoinType,
        Count = 1
    };

    private static AnalyticsContributionEventDetails DisplayEvent(RecordedEventEnvelope source, long quantity, decimal? amount, string priceSource) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Sequence = source.Sequence,
        EventId = source.EventId,
        Kind = source.Kind,
        Command = source.Command,
        ReceivedAtUtc = source.ReceivedAtUtc,
        SourceTimeUtc = source.SourceTimeUtc,
        OffsetMs = source.OffsetMs,
        UserId = source.UserId,
        UserName = source.UserName,
        Text = source.Text,
        AvatarUrl = source.AvatarUrl,
        Amount = amount,
        Quantity = quantity,
        TransactionId = source.TransactionId,
        CoinType = source.CoinType,
        PriceSource = priceSource,
        Payload = source.Payload
    };

    private static string GroupIdentity(AnalyticsContribution item)
    {
        var user = UserIdentity(item);
        var project = item.Kind == "Gift" ? string.IsNullOrWhiteSpace(item.GiftId) ? item.GiftName : item.GiftId
            : item.Kind == "Guard" ? item.GuardLevel?.ToString(CultureInfo.InvariantCulture) ?? item.Text : "";
        return JsonSerializer.Serialize(new[] { item.Kind, user, project, item.Kind == "Gift" ? item.CoinType?.ToLowerInvariant() : "" });
    }

    private static string UserIdentity(AnalyticsContribution item) =>
        AnalyticsContributionEvent.ValidUser(item.UserId) ? "user:" + item.UserId : "unidentified:" + item.Id;

    private static AnalyticsContribution User(IGrouping<string, AnalyticsContribution> group)
    {
        var result = Group(group);
        var records = group.ToList();
        var pockets = records.Where(item => item.Kind == "RedPocket").ToList();
        result.Kind = "All";
        result.Text = "";
        result.GiftId = "";
        result.GiftName = "";
        result.GuardLevel = null;
        result.CoinType = null;
        result.Amount = result.UnknownAmountCount == 0 ? result.KnownAmount : null;
        result.TotalBattery = pockets.All(item => item.TotalBattery.HasValue) ? pockets.Sum(item => item.TotalBattery ?? 0) : null;
        result.Categories = Kinds.Select(kind => Category(kind, records.Where(item => item.Kind == kind).ToList())).ToList();
        return result;
    }

    private static AnalyticsContributionCategory Category(string kind, List<AnalyticsContribution> records)
    {
        var knownAmount = records.Sum(item => item.KnownAmount);
        var unknown = records.Sum(item => item.UnknownAmountCount);
        return new()
        {
            Kind = kind,
            Count = records.Sum(item => item.Count),
            Quantity = records.Sum(item => item.Quantity),
            Amount = unknown == 0 ? knownAmount : null,
            KnownAmount = knownAmount,
            QuotedAmount = records.Sum(item => item.QuotedAmount ?? 0),
            UnknownAmountCount = unknown,
            TotalBattery = kind == "RedPocket" && records.All(item => item.TotalBattery.HasValue) ? records.Sum(item => item.TotalBattery ?? 0) : null,
            Projects = kind is "Gift" or "Guard" ? records.GroupBy(GroupIdentity, StringComparer.Ordinal).Select(Group).Select(item => new AnalyticsContributionProject
            {
                GiftId = item.GiftId,
                GiftName = item.GiftName,
                GuardLevel = item.GuardLevel,
                CoinType = item.CoinType,
                Text = item.Text,
                Count = item.Count,
                Quantity = item.Quantity,
                Amount = item.Amount,
                KnownAmount = item.KnownAmount,
                QuotedAmount = item.QuotedAmount ?? 0,
                UnknownAmountCount = item.UnknownAmountCount
            }).ToList() : []
        };
    }

    private static AnalyticsContribution Group(IGrouping<string, AnalyticsContribution> group)
    {
        var records = group.ToList();
        var first = records[0];
        var last = records[^1];
        var knownAmount = records.Sum(item => item.KnownAmount);
        return new()
        {
            Id = group.Key,
            Kind = first.Kind,
            UserId = first.UserId,
            UserName = records.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.UserName))?.UserName ?? "",
            AvatarUrl = records.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.AvatarUrl))?.AvatarUrl,
            OffsetMs = first.OffsetMs,
            Sequence = first.Sequence,
            Text = last.Text,
            GiftId = first.GiftId,
            GiftName = records.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.GiftName))?.GiftName ?? "",
            GuardLevel = first.GuardLevel,
            Quantity = records.Sum(item => item.Quantity),
            Count = records.Sum(item => item.Count),
            Amount = records.Any(item => item.Amount.HasValue) && records.All(item => item.Amount.HasValue || item.QuotedAmount.HasValue) ? records.Sum(item => item.Amount ?? 0) : null,
            QuotedAmount = records.Any(item => item.QuotedAmount.HasValue) ? records.Sum(item => item.QuotedAmount ?? 0) : null,
            CoinType = first.CoinType,
            PriceSource = records.Select(item => item.PriceSource).Distinct(StringComparer.Ordinal).Take(2).Count() == 1 ? first.PriceSource : "Mixed",
            TotalBattery = first.Kind == "RedPocket" && records.All(item => item.TotalBattery.HasValue) ? records.Sum(item => item.TotalBattery ?? 0) : null,
            KnownAmount = knownAmount,
            UnknownAmountCount = records.Sum(item => item.UnknownAmountCount)
        };
    }
}
