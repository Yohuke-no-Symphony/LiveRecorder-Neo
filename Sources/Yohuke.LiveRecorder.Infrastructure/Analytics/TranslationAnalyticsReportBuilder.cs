namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class TranslationAnalyticsReportBuilder
{
    internal const int BUCKET_WIDTH_MS = 60000;
    private readonly TranslationAnalyticsReport report = new();
    private readonly Dictionary<string, TranslationAnalyticsUser> users = new(StringComparer.Ordinal);
    private readonly Dictionary<long, TranslationAnalyticsBucket> buckets = new();

    internal void Add(AnalyticsInteractionObservation interaction)
    {
        report.Count++;
        if (!buckets.TryGetValue(interaction.BucketKey, out var bucket))
        {
            bucket = new() { OffsetMs = interaction.BucketKey * (double)BUCKET_WIDTH_MS };
            buckets.Add(interaction.BucketKey, bucket);
        }
        bucket.Count++;
        if (string.IsNullOrWhiteSpace(interaction.UserId) || interaction.UserId == "0")
        {
            return;
        }
        if (!users.TryGetValue(interaction.UserId, out var user))
        {
            user = new() { UserId = interaction.UserId };
            users.Add(interaction.UserId, user);
        }
        if (!string.IsNullOrWhiteSpace(interaction.UserName))
        {
            user.UserName = interaction.UserName;
        }
        user.Count++;
    }

    internal TranslationAnalyticsReport Build(int rankingLimit = AnalyticsReport.USER_RANKING_LIMIT)
    {
        report.Users = users.Count;
        report.TopUsers = users.Values.OrderByDescending(user => user.Count)
            .ThenBy(user => user.UserId, StringComparer.Ordinal).Take(rankingLimit).ToList();
        report.Buckets = buckets.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        return report;
    }
}
