using System.Text.Json;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal static class AnalyticsCacheValidator
{
    private static IReadOnlyList<string> CategoryKinds { get; } = ["SuperChat", "Gift", "Guard"];

    private static IReadOnlyList<string> RankingKeys { get; } =
    [
        nameof(AnalyticsUser.DanmuCount),
        nameof(AnalyticsUser.SuperChatAmount),
        nameof(AnalyticsUser.SuperChatCount),
        nameof(AnalyticsUser.GiftAmount),
        nameof(AnalyticsUser.GiftQuantity),
        nameof(AnalyticsUser.GuardAmount),
        nameof(AnalyticsUser.GuardQuantity),
        nameof(AnalyticsUser.RedPocketAmount),
        nameof(AnalyticsUser.RedPocketCount)
    ];

    internal static bool IsComplete(AnalyticsReport report)
    {
        if (report.Categories is not { } categories || categories.Count != CategoryKinds.Count ||
            report.UserRankings is not { } rankings || rankings.Count != RankingKeys.Count ||
            report.Buckets is not { } buckets || !HasValidUsers(report.TopUsers))
        {
            return false;
        }

        var byKind = new Dictionary<string, AnalyticsCategorySummary>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            if (!IsKnownCategory(category) ||
                !byKind.TryAdd(category.Kind, category))
            {
                return false;
            }
        }

        foreach (var metric in RankingKeys)
        {
            if (!rankings.TryGetValue(JsonNamingPolicy.CamelCase.ConvertName(metric), out var users) ||
                !HasValidUsers(users))
            {
                return false;
            }
        }

        if (!HasValidBuckets(buckets) || !HasValidTranslations(report.Translations))
        {
            return false;
        }

        var gift = byKind["Gift"];
        var superChat = byKind["SuperChat"];
        var guard = byKind["Guard"];
        var hasPayers = rankings["superChatAmount"].Count > 0 || rankings["giftAmount"].Count > 0 ||
                        rankings["guardAmount"].Count > 0;
        try
        {
            return gift.Amount == report.GoldGiftValue &&
                   superChat.Amount + guard.Amount == report.ProtocolPaidAmount &&
                   gift.Amount + superChat.Amount + guard.Amount == report.ObservedAmount &&
                   gift.Count == report.GiftCount && superChat.Count == report.SuperChatCount &&
                   guard.Count == report.GuardCount && gift.Quantity == report.GiftQuantity &&
                   superChat.Quantity == report.SuperChatCount &&
                   buckets.Sum(bucket => (long)bucket.DanmuCount) == report.DanmuCount &&
                   buckets.Sum(bucket => bucket.PaidAmount) == report.ObservedAmount &&
                   buckets.All(bucket =>
                       bucket.PaidAmount == bucket.SuperChatAmount + bucket.GiftAmount + bucket.GuardAmount) &&
                   report.PayingUsers >= 0 && report.PayingUsers <= report.ActiveUsers &&
                   report.DanmuUsers >= 0 && report.DanmuUsers <= report.ActiveUsers &&
                   (report.PayingUsers > 0) == hasPayers &&
                   (report.DanmuUsers > 0) == (rankings["danmuCount"].Count > 0);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool IsKnownCategory(AnalyticsCategorySummary? category) =>
        category is not null && CategoryKinds.Contains(category.Kind, StringComparer.Ordinal);

    private static bool HasValidUsers(IEnumerable<AnalyticsUser?>? users) =>
        users is not null && users.All(user => user is not null);

    private static bool HasValidBuckets(IEnumerable<AnalyticsBucket?>? buckets) =>
        buckets is not null && buckets.All(bucket =>
            bucket is not null && double.IsFinite(bucket.OffsetMs) && bucket.OffsetMs >= 0);

    private static bool HasValidTranslations(TranslationAnalyticsReport? translations)
    {
        if (translations is null || translations.Count < 0 || translations.Users < 0 ||
            translations.Users > translations.Count || translations.TopUsers is not { } users ||
            users.Count != Math.Min(translations.Users, AnalyticsReport.USER_RANKING_LIMIT) || translations.Buckets is not { } buckets)
        {
            return false;
        }
        return HasValidTranslationUsers(users, translations.Users, translations.Count) &&
               HasValidTranslationBuckets(buckets, translations.Count);
    }

    private static bool HasValidTranslationUsers(IReadOnlyList<TranslationAnalyticsUser?> users, int totalUsers, int totalCount)
    {
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        TranslationAnalyticsUser? previousUser = null;
        long userCount = 0;
        foreach (var user in users)
        {
            if (user is null || !HasValidTranslationIdentity(user.UserId, user.UserName) ||
                user.Count <= 0 || !userIds.Add(user.UserId) ||
                previousUser is not null && (previousUser.Count < user.Count || previousUser.Count == user.Count &&
                    StringComparer.Ordinal.Compare(previousUser.UserId, user.UserId) >= 0))
            {
                return false;
            }
            userCount += user.Count;
            previousUser = user;
        }
        return userCount <= (long)totalCount - (totalUsers - users.Count);
    }

    private static bool HasValidTranslationIdentity(string? userId, string? userName) =>
        !string.IsNullOrWhiteSpace(userId) && userId != "0" && userName is not null;

    private static bool HasValidTranslationBuckets(IEnumerable<TranslationAnalyticsBucket?> buckets, int totalCount)
    {
        var previousOffset = -1d;
        long count = 0;
        foreach (var bucket in buckets)
        {
            if (bucket is null || !double.IsFinite(bucket.OffsetMs) || bucket.OffsetMs < 0 ||
                bucket.OffsetMs % TranslationAnalyticsReportBuilder.BUCKET_WIDTH_MS != 0 || bucket.OffsetMs <= previousOffset || bucket.Count <= 0)
            {
                return false;
            }
            count += bucket.Count;
            previousOffset = bucket.OffsetMs;
        }
        return count == totalCount;
    }
}
