using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证来源分类、独立用户榜单和分钟互动统计。</summary>
public sealed class AnalyticsAudienceTests
{
    /// <summary>按业务来源统计，报价和免费礼物不增加流水。</summary>
    [Fact]
    public void CategoriesSeparateSuperChatsGiftsAndGuardPayments()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Danmu("danmu", "1"));
        accumulator.Add(Gift("gift", "1", 10m, 3));
        var silver = Gift("silver", "2", 100m, 5);
        silver.CoinType = "silver";
        silver.PriceSource = "NonMonetary";
        accumulator.Add(silver);
        accumulator.Add(SuperChat("sc", "3", 30m));
        accumulator.Add(Guard("guard", "4", "ProtocolQuote", 198m, 2));
        accumulator.Add(Guard("guard", "4", "ProtocolPaid", 138m, 2));
        accumulator.Add(RedPocket("red", "pocket", "5"));

        var report = accumulator.Build();
        var gift = Assert.Single(report.Categories, category => category.Kind == "Gift");
        var sc = Assert.Single(report.Categories, category => category.Kind == "SuperChat");
        var guard = Assert.Single(report.Categories, category => category.Kind == "Guard");
        Assert.Equal(198m, report.ObservedAmount);
        Assert.Equal(report.ObservedAmount, report.Categories.Sum(category => category.Amount));
        Assert.Equal(30m, gift.Amount);
        Assert.Equal(2, gift.Count);
        Assert.Equal(8, gift.Quantity);
        Assert.Equal(2, gift.Users);
        Assert.Equal(30m, sc.Amount);
        Assert.Equal(1, sc.Count);
        Assert.Equal(1, sc.Quantity);
        Assert.Equal(138m, guard.Amount);
        Assert.Equal(1, guard.Count);
        Assert.Equal(2, guard.Quantity);
        Assert.Equal(1, guard.Users);
        Assert.Equal(5, report.ActiveUsers);
        Assert.Equal(3, report.PayingUsers);
        Assert.Equal(1, report.DanmuUsers);
        Assert.Equal(1, Assert.Single(report.UserRankings["danmuCount"]).DanmuCount);
        Assert.Equal(8, report.TopUsers.Sum(user => user.GiftQuantity));
        Assert.Equal(report.ObservedAmount, report.TopUsers.Sum(user => user.PaidAmount));
    }

    /// <summary>各维度从全量用户生成榜单，少发言的支持者不会被互动榜前百截断。</summary>
    [Fact]
    public void RankingsConsiderQuietSupportersOutsideTheInteractionTopHundred()
    {
        var accumulator = new AnalyticsAccumulator();
        for (var index = 0; index < 101; index++)
        {
            accumulator.Add(Danmu($"first-{index}", $"talker-{index:D3}"));
            accumulator.Add(Danmu($"second-{index}", $"talker-{index:D3}"));
        }
        accumulator.Add(SuperChat("sc", "sc-supporter", 1000m));
        accumulator.Add(Gift("gift", "gift-supporter", 20m, 5));
        accumulator.Add(Guard("guard", "guard-supporter", "ProtocolPaid", 138m, 2));
        var pocket = RedPocket("pocket", "pocket", "pocket-supporter");
        pocket.Amount = 10m;
        pocket.PriceSource = "ProtocolPaid";
        accumulator.Add(pocket);

        var report = accumulator.Build();
        Assert.Equal(100, report.TopUsers.Count);
        Assert.DoesNotContain(report.TopUsers, user => user.UserId.EndsWith("supporter", StringComparison.Ordinal));
        Assert.Equal(9, report.UserRankings.Count);
        Assert.Equal(100, report.UserRankings["danmuCount"].Count);
        foreach (var metric in new[] { "superChatAmount", "superChatCount" })
        {
            Assert.Equal("sc-supporter", Assert.Single(report.UserRankings[metric]).UserId);
        }
        foreach (var metric in new[] { "giftAmount", "giftQuantity" })
        {
            Assert.Equal("gift-supporter", Assert.Single(report.UserRankings[metric]).UserId);
        }
        foreach (var metric in new[] { "guardAmount", "guardQuantity" })
        {
            Assert.Equal("guard-supporter", Assert.Single(report.UserRankings[metric]).UserId);
        }
        foreach (var metric in new[] { "redPocketAmount", "redPocketCount" })
        {
            Assert.Equal("pocket-supporter", Assert.Single(report.UserRankings[metric]).UserId);
        }
        Assert.Equal("talker-000", report.UserRankings["danmuCount"][0].UserId);
    }

    /// <summary>分钟桶分别记录弹幕数、独立互动人数和付费人数，并排除重复支付。</summary>
    [Fact]
    public void MinuteBucketsSeparateSpeakersParticipantsAndPayingUsers()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Danmu("first", "1"));
        accumulator.Add(Danmu("second", "1"));
        accumulator.Add(Gift("gift-a", "1", 1m));
        accumulator.Add(Gift("gift-b", "2", 2m));
        var sc = SuperChat("sc", "1", 30m);
        sc.OffsetMs = 60000;
        accumulator.Add(sc);
        accumulator.Add(sc);
        var quote = Guard("guard", "3", "ProtocolQuote", 198m);
        quote.OffsetMs = 61000;
        var paid = Guard("guard", "3", "ProtocolPaid", 138m);
        paid.OffsetMs = 62000;
        accumulator.Add(quote);
        accumulator.Add(paid);

        var report = accumulator.Build();
        Assert.Equal(2, report.Buckets.Count);
        var first = report.Buckets[0];
        Assert.Equal(4, first.Messages);
        Assert.Equal(2, first.DanmuCount);
        Assert.Equal(2, first.ActiveUsers);
        Assert.Equal(1, first.DanmuUsers);
        Assert.Equal(2, first.PayingUsers);
        Assert.Equal(3m, first.GiftAmount);
        var second = report.Buckets[1];
        Assert.Equal(2, second.Messages);
        Assert.Equal(0, second.DanmuCount);
        Assert.Equal(2, second.ActiveUsers);
        Assert.Equal(0, second.DanmuUsers);
        Assert.Equal(2, second.PayingUsers);
        Assert.Equal(30m, second.SuperChatAmount);
        Assert.Equal(138m, second.GuardAmount);
        Assert.Equal(report.ObservedAmount, report.Buckets.Sum(bucket => bucket.PaidAmount));
        Assert.All(report.Buckets, bucket => Assert.Equal(bucket.PaidAmount, bucket.SuperChatAmount + bucket.GiftAmount + bucket.GuardAmount));
    }

    /// <summary>红包多阶段更新合并为一笔，补齐发包者后不猜测原始金额单位。</summary>
    [Fact]
    public void RedPocketStagesEnrichTheSenderWithoutGuessingCurrencyUnits()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(RedPocket("new", "same-pocket", ""));
        var detail = RedPocket("details", "same-pocket", "7");
        detail.OffsetMs = 61000;
        detail.Payload!["redPocket"] = new JsonObject { ["lotId"] = "same-pocket", ["price"] = 1000m, ["totalPrice"] = 20000m, ["totalBattery"] = 2500m };
        accumulator.Add(detail);
        accumulator.Add(RedPocket("winner-list", "same-pocket", "7"));

        var report = accumulator.Build();
        var user = Assert.Single(report.TopUsers);
        Assert.Equal(1, report.RedPocketCount);
        Assert.Equal(1, report.ActiveUsers);
        Assert.Equal(0, report.PayingUsers);
        Assert.Equal("7", user.UserId);
        Assert.Equal(1, user.Messages);
        Assert.Equal(1, user.RedPocketCount);
        Assert.Null(user.RedPocketAmount);
        Assert.Empty(report.UserRankings["redPocketAmount"]);
        Assert.Equal("7", Assert.Single(report.UserRankings["redPocketCount"]).UserId);
        var bucket = Assert.Single(report.Buckets);
        Assert.Equal(0, bucket.OffsetMs);
        Assert.Equal(1, bucket.Messages);
        Assert.Equal(1, bucket.ActiveUsers);
        Assert.Equal(0m, report.ObservedAmount);
    }

    /// <summary>已明确归一化的红包金额可独立排名，但不能混入主播流水。</summary>
    [Fact]
    public void ExplicitRedPocketAmountsAreDeduplicatedAndExcludedFromRevenue()
    {
        var accumulator = new AnalyticsAccumulator();
        var pocket = RedPocket("first", "same", "1");
        pocket.Amount = 10m;
        pocket.PriceSource = "ProtocolPaid";
        accumulator.Add(pocket);
        var enriched = RedPocket("second", "same", "1");
        enriched.Amount = 15m;
        enriched.PriceSource = "ProtocolPaid";
        accumulator.Add(enriched);
        accumulator.Add(enriched);
        var another = RedPocket("another", "another", "1");
        another.Amount = 5m;
        another.PriceSource = "ProtocolPaid";
        accumulator.Add(another);

        var report = accumulator.Build();
        var user = Assert.Single(report.UserRankings["redPocketAmount"]);
        Assert.Equal(20m, user.RedPocketAmount);
        Assert.Equal(2, user.RedPocketCount);
        Assert.Equal(0m, user.PaidAmount);
        Assert.Equal(0m, report.ObservedAmount);
        Assert.Equal(0, report.PayingUsers);
    }

    /// <summary>任何一笔红包价格未知时，用户红包总额仍保持未知而非伪装为完整总额。</summary>
    [Fact]
    public void UnknownRedPocketAmountKeepsTheUserTotalUnknown()
    {
        var accumulator = new AnalyticsAccumulator();
        var known = RedPocket("known", "known", "1");
        known.Amount = 10m;
        known.PriceSource = "ProtocolPaid";
        accumulator.Add(known);
        accumulator.Add(RedPocket("unknown", "unknown", "1"));

        var report = accumulator.Build();
        Assert.Null(Assert.Single(report.TopUsers).RedPocketAmount);
        Assert.Empty(report.UserRankings["redPocketAmount"]);
        Assert.Equal(2, Assert.Single(report.UserRankings["redPocketCount"]).RedPocketCount);
    }

    /// <summary>服务器购买 ID 即使缺少 Envelope EventId，也不能重复增加大航海次数或数量。</summary>
    [Fact]
    public void GuardServerIdentityDeduplicatesQuotePaymentAndQuantity()
    {
        var accumulator = new AnalyticsAccumulator();
        var quote = Guard("same", "1", "ProtocolQuote", 198m, 2);
        var paid = Guard("same", "1", "ProtocolPaid", 138m, 2);
        quote.EventId = "";
        paid.EventId = "";
        accumulator.Add(quote);
        accumulator.Add(paid);
        accumulator.Add(paid);

        var report = accumulator.Build();
        var category = Assert.Single(report.Categories, category => category.Kind == "Guard");
        Assert.Equal(1, report.GuardCount);
        Assert.Equal(1, category.Count);
        Assert.Equal(2, category.Quantity);
        Assert.Equal(2, Assert.Single(report.UserRankings["guardQuantity"]).GuardQuantity);
        Assert.Equal(138m, Assert.Single(report.UserRankings["guardAmount"]).GuardAmount);
        Assert.Equal(198m, report.QuotedAmount);
    }

    /// <summary>礼物服务器行 ID 在 Envelope 缺失 ID 时仍可去重互动、数量和流水。</summary>
    [Fact]
    public void GiftPayloadIdentityDeduplicatesInteractionsAndMoney()
    {
        var accumulator = new AnalyticsAccumulator();
        var gift = Gift("", "1", 2m, 3);
        gift.Payload!["messageId"] = "server-row";
        accumulator.Add(gift);
        accumulator.Add(gift);

        var report = accumulator.Build();
        var category = Assert.Single(report.Categories, category => category.Kind == "Gift");
        Assert.Equal(1, report.GiftCount);
        Assert.Equal(1, category.Count);
        Assert.Equal(3, category.Quantity);
        Assert.Equal(6m, category.Amount);
        Assert.Equal(1, Assert.Single(report.TopUsers).Messages);
    }

    /// <summary>重复生成快照不会共享可变计数，未知和零流水不会占据金额榜。</summary>
    [Fact]
    public void SnapshotsRemainIndependentAndEmptyAmountRankingsStayEmpty()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Danmu("first", "1"));
        accumulator.Add(Guard("quote", "2", "ProtocolQuote", 198m));
        var before = accumulator.Build();
        accumulator.Add(Danmu("second", "1"));
        var after = accumulator.Build();

        Assert.Equal(1, Assert.Single(before.UserRankings["danmuCount"]).DanmuCount);
        Assert.Equal(2, Assert.Single(after.UserRankings["danmuCount"]).DanmuCount);
        Assert.Empty(before.UserRankings["guardAmount"]);
        Assert.Empty(after.UserRankings["giftAmount"]);
        Assert.Equal(0, before.PayingUsers);
        Assert.Equal(3, before.Categories.Count);
    }

    private static RecordedEventEnvelope Danmu(string id, string userId) => new()
    {
        EventId = id, Kind = "Danmu", UserId = userId, UserName = userId
    };

    private static RecordedEventEnvelope Gift(string id, string userId, decimal amount, int quantity = 1)
    {
        var result = AnalyticsFinancialTests.Gift(id, "gift", amount, quantity);
        result.UserId = userId;
        result.UserName = userId;
        return result;
    }

    private static RecordedEventEnvelope SuperChat(string id, string userId, decimal amount) => new()
    {
        EventId = id, Kind = "SuperChat", UserId = userId, UserName = userId, Amount = amount, PriceSource = "ProtocolPaid"
    };

    private static RecordedEventEnvelope Guard(string id, string userId, string source, decimal amount, int quantity = 1)
    {
        var result = AnalyticsFinancialTests.Guard(id, source, amount);
        result.UserId = userId;
        result.UserName = userId;
        result.Quantity = quantity;
        result.Payload!["quantity"] = quantity;
        return result;
    }

    private static RecordedEventEnvelope RedPocket(string id, string transactionId, string userId) => new()
    {
        EventId = id, Kind = "RedPocket", TransactionId = transactionId, UserId = userId, UserName = userId,
        Payload = new JsonObject { ["roomId"] = "42" }
    };
}
