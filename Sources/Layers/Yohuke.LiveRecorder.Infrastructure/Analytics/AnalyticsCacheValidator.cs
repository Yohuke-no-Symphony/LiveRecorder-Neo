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

        if (!HasValidBuckets(buckets) || !HasValidTranslations(report.Translations) || !HasValidOnline(report))
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
                   buckets.Sum(bucket => (long)bucket.SuperChatCount) == report.SuperChatCount &&
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
            bucket is not null && double.IsFinite(bucket.OffsetMs) && bucket.OffsetMs >= 0 && bucket.SuperChatCount >= 0);

    private static bool HasValidOnline(AnalyticsReport report)
    {
        if (!double.IsFinite(report.FromMs) || !double.IsFinite(report.ToMs) || report.FromMs < 0 ||
            report.ToMs < report.FromMs || report.Online is not { } online || online.Intervals is not { } intervals ||
            online.ObservedMs < 0 || online.CoverageRate is < 0 or > 1 ||
            !Near(online.RangeMs, report.ToMs - report.FromMs))
        {
            return false;
        }
        var previousEnd = report.FromMs;
        foreach (var interval in intervals)
        {
            if (interval is null || !double.IsFinite(interval.FromMs) || !double.IsFinite(interval.ToMs) ||
                interval.FromMs < previousEnd || interval.ToMs > report.ToMs || interval.ToMs <= interval.FromMs || interval.Count < 0)
            {
                return false;
            }
            previousEnd = interval.ToMs;
        }
        var expected = OnlineAnalyticsProjection.Summarize(intervals, report.ToMs - report.FromMs);
        if (online.Peak != expected.Peak || !SameAverage(online.Average, expected.Average) ||
            !Near(online.ObservedMs, expected.ObservedMs) || !Near(online.CoverageRate, expected.CoverageRate))
        {
            return false;
        }
        var previousOffset = -1d;
        var intervalIndex = 0;
        var bucketObserved = 0d;
        foreach (var bucket in report.Buckets)
        {
            var from = Math.Max(report.FromMs, bucket.OffsetMs);
            var to = Math.Min(report.ToMs, bucket.OffsetMs + OnlineAnalyticsProjection.BUCKET_WIDTH_MS);
            if (bucket.OffsetMs <= previousOffset || bucket.OffsetMs % OnlineAnalyticsProjection.BUCKET_WIDTH_MS != 0 ||
                to <= from || !Near(bucket.DurationMs, to - from))
            {
                return false;
            }
            while (intervalIndex < intervals.Count && intervals[intervalIndex].ToMs <= from)
            {
                intervalIndex++;
            }
            var observed = 0d;
            var area = 0d;
            long? peak = null;
            for (var index = intervalIndex; index < intervals.Count && intervals[index].FromMs < to; index++)
            {
                var interval = intervals[index];
                var duration = Math.Min(to, interval.ToMs) - Math.Max(from, interval.FromMs);
                observed += duration;
                area += interval.Count * duration;
                peak = Math.Max(peak ?? 0, interval.Count);
            }
            if (bucket.OnlineObservedMs < 0 || !Near(bucket.OnlineObservedMs, observed) || bucket.OnlinePeak != peak ||
                !SameAverage(bucket.OnlineAverage, observed > 0 ? area / observed : null))
            {
                return false;
            }
            bucketObserved += observed;
            previousOffset = bucket.OffsetMs;
        }
        return Near(bucketObserved, online.ObservedMs);
    }

    private static bool SameAverage(double? actual, double? expected) =>
        actual.HasValue == expected.HasValue && (!actual.HasValue || Near(actual.Value, expected!.Value));

    private static bool Near(double actual, double expected) =>
        double.IsFinite(actual) && double.IsFinite(expected) && Math.Abs(actual - expected) <= Math.Max(1, Math.Abs(expected)) * 0.000000001;

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
