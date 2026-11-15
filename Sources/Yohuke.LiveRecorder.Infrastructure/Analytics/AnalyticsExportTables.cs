using System.Globalization;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsExportTables(AnalyticsExportLabels labels)
{
    internal void Write(AnalyticsSpreadsheetWriter writer, SessionDetail detail, AnalyticsBuildData data, string? streamerName)
    {
        var report = data.Report;
        var contributions = data.Contributions ?? throw new InvalidOperationException("The export requires contribution observations.");
        writer.WriteTable(labels["overviewSheet"], [Column("metric", 34), Column("value", 48)], Overview(detail, report, contributions, streamerName));
        writer.WriteTable(labels["usersSheet"],
        [
            Column("userId", 24), Column("userName", 26), Number("count"), Amount("knownTotal"), Amount("quotedAmount"), Number("unknownAmountCount"),
            Number("superChatCount"), Amount("superChatAmount"), Number("superChatUnknown"),
            Number("giftCount"), Number("giftQuantity"), Amount("giftAmount"), Number("giftUnknown"),
            Number("guardCount"), Number("guardQuantity"), Amount("guardAmount"), Amount("guardQuote"), Number("guardUnknown"),
            Number("redPocketCount"), Number("totalBattery")
        ], UserRows(contributions.Users));
        writer.WriteTable(labels["projectsSheet"],
        [
            Column("userId", 24), Column("userName", 26), Column("kind", 16), Column("giftId", 24), Column("giftName", 28),
            Column("guardLevel", 18), Number("guardLevelNumber"), Column("coinType", 14), Number("count"), Number("quantity"),
            Amount("knownAmount"), Amount("quotedAmount"), Number("unknownAmountCount")
        ], ProjectRows(contributions.Users));
        WriteDetails(writer, contributions.Events);
        writer.WriteTable(labels["bucketsSheet"],
        [
            Time("from"), Time("to"), Number("fromMs"), Number("toMs"), Time("duration"), Number("messages"), Number("danmuCount"),
            Number("activeUsers"), Number("danmuUsers"), Number("payingUsers"), Amount("knownTotal"), Number("superChatCount"),
            Amount("superChatAmount"), Amount("giftAmount"), Amount("guardAmount"), Number("translationCount"),
            Number("onlineAverage"), Number("onlinePeak"), Time("onlineObserved"), Column("onlineCoverage", 20, AnalyticsSpreadsheetStyles.PERCENT)
        ], BucketRows(report));
        writer.WriteTable(labels["onlineSheet"], [Time("from"), Time("to"), Number("fromMs"), Number("toMs"), Time("duration"), Number("onlineCount")],
            report.Online.Intervals.Select(interval => new object?[]
            {
                Duration(interval.FromMs), Duration(interval.ToMs), interval.FromMs, interval.ToMs, Duration(interval.ToMs - interval.FromMs), interval.Count
            }));
        writer.WriteTable(labels["activeSheet"],
        [
            Number("rank"), Column("userId", 24), Column("userName", 26), Number("messages"), Number("danmuCount"), Amount("knownTotal"),
            Number("superChatCount"), Amount("superChatAmount"), Number("giftQuantity"), Amount("giftAmount"),
            Number("guardQuantity"), Amount("guardAmount"), Number("redPocketCount"), Amount("redPocketAmount")
        ], report.TopUsers.Select((user, index) => new object?[]
        {
            index + 1, user.UserId, user.UserName, user.Messages, user.DanmuCount, user.PaidAmount,
            user.SuperChatCount, user.SuperChatAmount, user.GiftQuantity, user.GiftAmount, user.GuardQuantity, user.GuardAmount,
            user.RedPocketCount, user.RedPocketAmount
        }));
        writer.WriteTable(labels["translatorsSheet"], [Number("rank"), Column("userId", 24), Column("userName", 26), Number("translationCount")],
            report.Translations.TopUsers.Select((user, index) => new object?[] { index + 1, user.UserId, user.UserName, user.Count }));
        writer.WriteTable(labels["rankingsSheet"],
            [Column("metric", 34), Number("rank"), Column("userId", 24), Column("userName", 26), Number("value"), Column("unit", 14)], RankingRows(report));
    }

    private IEnumerable<object?[]> Overview(SessionDetail detail, AnalyticsReport report, AnalyticsContributionSnapshot contributions, string? streamerName)
    {
        yield return [labels["sessionId"], detail.Session.Id];
        yield return [labels["streamer"], streamerName ?? detail.Session.StreamerId];
        yield return [labels["roomId"], detail.Session.RoomId.ToString(CultureInfo.InvariantCulture)];
        yield return [labels["title"], detail.Session.Title];
        yield return [labels["sessionStart"], Date(detail.Session.StartedAtUtc)];
        yield return [labels["sessionEnd"], Date(detail.Session.EndedAtUtc)];
        yield return [labels["builtAt"], Date(report.BuiltAtUtc)];
        yield return [labels["from"], Duration(report.FromMs)];
        yield return [labels["to"], Duration(report.ToMs)];
        yield return [labels["duration"], Duration(report.ToMs - report.FromMs)];
        yield return [labels["knownTotal"], Money(report.ObservedAmount)];
        yield return [labels["goldValue"], Money(report.GoldGiftValue)];
        yield return [labels["protocolPaid"], Money(report.ProtocolPaidAmount)];
        yield return [labels["quotedAmount"], Money(report.QuotedAmount)];
        yield return [labels["unknownAmountCount"], report.UnknownAmountCount];
        yield return [labels["unknownGiftQuantity"], report.UnknownGiftQuantity];
        yield return [labels["unidentifiedFinancial"], report.UnidentifiedFinancialEvents];
        yield return [labels["activeUsers"], report.ActiveUsers];
        yield return [labels["payingUsers"], report.PayingUsers];
        yield return [labels["payingRate"], new AnalyticsExportCell(report.ActiveUsers > 0 ? (double)report.PayingUsers / report.ActiveUsers : null, AnalyticsSpreadsheetStyles.PERCENT)];
        yield return [labels["danmuUsers"], report.DanmuUsers];
        yield return [labels["danmuCount"], report.DanmuCount];
        yield return [labels["superChatCount"], report.SuperChatCount];
        yield return [labels["giftCount"], report.GiftCount];
        yield return [labels["giftQuantity"], report.GiftQuantity];
        yield return [labels["guardCount"], report.GuardCount];
        yield return [labels["guardQuantity"], report.Categories.FirstOrDefault(category => category.Kind == "Guard")?.Quantity ?? 0];
        yield return [labels["redPocketCount"], report.RedPocketCount];
        yield return [labels["totalBattery"], contributions.Summary.TotalBattery];
        yield return [labels["coverageGaps"], report.CoverageGaps];
        yield return [labels["onlinePeak"], report.Online.Peak];
        yield return [labels["onlineAverage"], new AnalyticsExportCell(report.Online.Average, AnalyticsSpreadsheetStyles.NUMBER)];
        yield return [labels["onlineObserved"], Duration(report.Online.ObservedMs)];
        yield return [labels["onlineCoverage"], new AnalyticsExportCell(report.Online.CoverageRate, AnalyticsSpreadsheetStyles.PERCENT)];
        yield return [labels["translationCount"], report.Translations.Count];
        yield return [labels["translationUsers"], report.Translations.Users];
    }

    private static IEnumerable<object?[]> UserRows(IEnumerable<AnalyticsContribution> users)
    {
        foreach (var user in users)
        {
            var sc = user.Categories.First(category => category.Kind == "SuperChat");
            var gift = user.Categories.First(category => category.Kind == "Gift");
            var guard = user.Categories.First(category => category.Kind == "Guard");
            var pocket = user.Categories.First(category => category.Kind == "RedPocket");
            yield return
            [
                user.UserId, user.UserName, user.Count, user.KnownAmount, user.QuotedAmount, user.UnknownAmountCount,
                sc.Count, sc.KnownAmount, sc.UnknownAmountCount, gift.Count, gift.Quantity, gift.KnownAmount, gift.UnknownAmountCount,
                guard.Count, guard.Quantity, guard.KnownAmount, guard.QuotedAmount, guard.UnknownAmountCount, pocket.Count, pocket.TotalBattery
            ];
        }
    }

    private IEnumerable<object?[]> ProjectRows(IEnumerable<AnalyticsContribution> users)
    {
        foreach (var user in users)
        {
            foreach (var category in user.Categories)
            {
                foreach (var project in category.Projects)
                {
                    yield return
                    [
                        user.UserId, user.UserName, labels[category.Kind], project.GiftId, project.GiftName, GuardLevel(project.GuardLevel),
                        project.GuardLevel, project.CoinType, project.Count, project.Quantity, project.KnownAmount, project.QuotedAmount, project.UnknownAmountCount
                    ];
                }
            }
        }
    }

    private void WriteDetails(AnalyticsSpreadsheetWriter writer, IEnumerable<AnalyticsContribution> events)
    {
        var records = events.ToLookup(item => item.Kind, StringComparer.Ordinal);
        writer.WriteTable(labels["scSheet"],
        [
            Time("offset"), Number("offsetMs"), Column("userId", 24), Column("userName", 26), Column("text", 70),
            Amount("knownAmount"), Amount("quotedAmount"), Number("unknownAmountCount"), Column("priceSource", 24),
            Column("receivedAt", 30), Column("sourceTime", 30), Column("eventId", 30), Column("transactionId", 26)
        ], records["SuperChat"].Select(item => new object?[]
        {
            Duration(item.OffsetMs), item.OffsetMs, item.UserId, item.UserName, item.Text,
            KnownAmount(item), item.QuotedAmount, item.UnknownAmountCount, PriceSource(item.PriceSource),
            Date(item.Event?.ReceivedAtUtc), Date(item.Event?.SourceTimeUtc), item.Event?.EventId, item.Event?.TransactionId
        }));
        writer.WriteTable(labels["giftsSheet"],
        [
            Time("offset"), Number("offsetMs"), Column("userId", 24), Column("userName", 26), Column("giftId", 24), Column("giftName", 28),
            Number("quantity"), Column("coinType", 14), Amount("knownAmount"), Amount("quotedAmount"), Number("unknownAmountCount"), Column("priceSource", 24),
            Column("receivedAt", 30), Column("sourceTime", 30), Column("eventId", 30), Column("transactionId", 26), Column("text", 50)
        ], records["Gift"].Select(item => new object?[]
        {
            Duration(item.OffsetMs), item.OffsetMs, item.UserId, item.UserName, item.GiftId, item.GiftName, item.Quantity, item.CoinType,
            KnownAmount(item), item.QuotedAmount, item.UnknownAmountCount, PriceSource(item.PriceSource), Date(item.Event?.ReceivedAtUtc),
            Date(item.Event?.SourceTimeUtc), item.Event?.EventId, item.Event?.TransactionId, item.Text
        }));
        writer.WriteTable(labels["pocketsSheet"],
        [
            Time("offset"), Number("offsetMs"), Column("senderId", 24), Column("senderName", 26), Column("lotId", 26), Number("totalBattery"),
            Amount("redPocketAmount"), Column("drawState", 26), Number("awardCount"), Column("awards", 55), Column("winners", 65), Column("text", 50),
            Column("receivedAt", 30), Column("sourceTime", 30), Column("eventId", 30)
        ], PocketRows(records["RedPocket"]));
        writer.WriteTable(labels["guardsSheet"],
        [
            Time("offset"), Number("offsetMs"), Column("userId", 24), Column("userName", 26), Column("guardLevel", 18), Number("guardLevelNumber"),
            Number("quantity"), Amount("knownAmount"), Amount("quotedAmount"), Number("unknownAmountCount"), Column("priceSource", 24),
            Column("receivedAt", 30), Column("sourceTime", 30), Column("eventId", 30), Column("transactionId", 26), Column("text", 50)
        ], records["Guard"].Select(item => new object?[]
        {
            Duration(item.OffsetMs), item.OffsetMs, item.UserId, item.UserName, GuardLevel(item.GuardLevel), item.GuardLevel,
            item.Quantity, KnownAmount(item), item.QuotedAmount, item.UnknownAmountCount, PriceSource(item.PriceSource),
            Date(item.Event?.ReceivedAtUtc), Date(item.Event?.SourceTimeUtc), item.Event?.EventId, item.Event?.TransactionId, item.Text
        }));
    }

    private IEnumerable<object?[]> PocketRows(IEnumerable<AnalyticsContribution> events)
    {
        foreach (var item in events)
        {
            var payload = item.Event?.Payload;
            var pocket = payload?["redPocket"] as JsonObject;
            var lotId = AnalyticsContributionEvent.Text(pocket, "lotId");
            var hasResult = pocket?["hasResult"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            var isDrawn = payload?["isDrawn"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            yield return
            [
                Duration(item.OffsetMs), item.OffsetMs, item.UserId, item.UserName, string.IsNullOrWhiteSpace(lotId) ? item.Event?.TransactionId : lotId,
                item.TotalBattery, item.Amount, labels[hasResult ? "drawn" : isDrawn ? "drawnWithoutResult" : "pending"],
                AnalyticsContributionEvent.Number(pocket, "totalAwardCount"), pocket?["awards"]?.ToJsonString(), hasResult ? pocket?["winners"]?.ToJsonString() : null,
                item.Text, Date(item.Event?.ReceivedAtUtc), Date(item.Event?.SourceTimeUtc), item.Event?.EventId
            ];
        }
    }

    private static IEnumerable<object?[]> BucketRows(AnalyticsReport report)
    {
        var buckets = report.Buckets.ToDictionary(bucket => bucket.OffsetMs);
        var translations = report.Translations.Buckets.ToDictionary(bucket => bucket.OffsetMs);
        foreach (var offset in buckets.Keys.Concat(translations.Keys).Distinct().Order())
        {
            var from = Math.Max(offset, report.FromMs);
            var to = Math.Min(offset + OnlineAnalyticsProjection.BUCKET_WIDTH_MS, report.ToMs);
            if (to <= from)
            {
                continue;
            }
            buckets.TryGetValue(offset, out var bucket);
            translations.TryGetValue(offset, out var translation);
            var observed = bucket?.OnlineObservedMs ?? 0;
            yield return
            [
                Duration(from), Duration(to), from, to, Duration(to - from), bucket?.Messages ?? 0, bucket?.DanmuCount ?? 0,
                bucket?.ActiveUsers ?? 0, bucket?.DanmuUsers ?? 0, bucket?.PayingUsers ?? 0, bucket?.PaidAmount ?? 0,
                bucket?.SuperChatCount ?? 0, bucket?.SuperChatAmount ?? 0, bucket?.GiftAmount ?? 0, bucket?.GuardAmount ?? 0, translation?.Count ?? 0,
                bucket?.OnlineAverage, bucket?.OnlinePeak, Duration(observed), observed / (to - from)
            ];
        }
    }

    private IEnumerable<object?[]> RankingRows(AnalyticsReport report)
    {
        foreach (var (metric, users) in report.UserRankings)
        {
            var rank = 0;
            foreach (var user in users)
            {
                var amount = metric.EndsWith("Amount", StringComparison.Ordinal);
                decimal? value = metric switch
                {
                    "danmuCount" => user.DanmuCount,
                    "superChatAmount" => user.SuperChatAmount,
                    "superChatCount" => user.SuperChatCount,
                    "giftAmount" => user.GiftAmount,
                    "giftQuantity" => user.GiftQuantity,
                    "guardAmount" => user.GuardAmount,
                    "guardQuantity" => user.GuardQuantity,
                    "redPocketAmount" => user.RedPocketAmount,
                    "redPocketCount" => user.RedPocketCount,
                    _ => throw new InvalidOperationException("Unsupported analytics ranking: " + metric)
                };
                yield return [labels[metric], ++rank, user.UserId, user.UserName,
                    new AnalyticsExportCell(value, amount ? AnalyticsSpreadsheetStyles.AMOUNT : AnalyticsSpreadsheetStyles.NUMBER), labels[amount ? "cny" : "number"]];
            }
        }
    }

    private string GuardLevel(int? level) => level is >= 1 and <= 3 ? labels["guard" + level.Value.ToString(CultureInfo.InvariantCulture)] : "";
    private string PriceSource(string source) => labels[source is "ProtocolGold" or "ProtocolPaid" or "ProtocolQuote" or "NonMonetary" or "Mixed" ? source : "Unknown"];
    private AnalyticsExportColumn Column(string key, double width = 20, uint style = AnalyticsSpreadsheetStyles.DEFAULT) => new(labels[key], width, style);
    private AnalyticsExportColumn Number(string key) => Column(key, 20, AnalyticsSpreadsheetStyles.NUMBER);
    private AnalyticsExportColumn Amount(string key) => Column(key, 24, AnalyticsSpreadsheetStyles.AMOUNT);
    private AnalyticsExportColumn Time(string key) => Column(key, 21, AnalyticsSpreadsheetStyles.DURATION);
    private static decimal? KnownAmount(AnalyticsContribution item) => item.Amount.HasValue || item.KnownAmount != 0 ? item.KnownAmount : null;
    private static AnalyticsExportCell Duration(double milliseconds) => new(milliseconds / 86400000d, AnalyticsSpreadsheetStyles.DURATION);
    private static AnalyticsExportCell Money(decimal value) => new(value, AnalyticsSpreadsheetStyles.AMOUNT);
    private static object? Date(DateTime? value) => value is null || value == default(DateTime) ? null : value.Value.Year < 1900 ? value.Value.ToString("O", CultureInfo.InvariantCulture) : value;
}
