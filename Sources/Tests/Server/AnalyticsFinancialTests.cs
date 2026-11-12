using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>按共享协议的事务、礼物行、连击及报价语义验证观测流水。</summary>
public sealed class AnalyticsFinancialTests
{
    /// <summary>一笔批次事务内不同礼物必须分别计值。</summary>
    [Fact]
    public void SameTransactionDifferentGiftIdsAreBothCounted()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Gift("row-a", "a", 3, transaction: "batch"));
        accumulator.Add(Gift("row-b", "b", 5, transaction: "batch"));
        Assert.Equal(8m, accumulator.Build().PaidRevenue);
    }

    /// <summary>V2 的同事务同礼物 occurrence 也是独立行，只有重推相同行才去重。</summary>
    [Fact]
    public void SameTransactionSameGiftDistinctOccurrencesRemainDistinct()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Gift("gift:batch:42:0", "42", 3, transaction: "batch"));
        accumulator.Add(Gift("gift:batch:42:1", "42", 3, transaction: "batch"));
        accumulator.Add(Gift("gift:batch:42:0", "42", 3, transaction: "batch"));
        Assert.Equal(6m, accumulator.Build().PaidRevenue);
    }

    /// <summary>连击累计数采用差值，增量包加数量，乱序累计和重复增量不能改变结果。</summary>
    [Fact]
    public void CumulativeAndIncrementalComboEventsUseQuantityDelta()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Gift("first", "gift", 10, quantity: 1, combo: "combo", cumulative: 4));
        accumulator.Add(Gift("old", "gift", 10, quantity: 1, combo: "combo", cumulative: 2));
        accumulator.Add(Gift("increment", "gift", 10, quantity: 2, combo: "combo"));
        accumulator.Add(Gift("increment", "gift", 10, quantity: 2, combo: "combo"));
        accumulator.Add(Gift("snapshot", "gift", 10, quantity: 1, combo: "combo", cumulative: 6));
        Assert.Equal(60m, accumulator.Build().PaidRevenue);
    }

    /// <summary>相同连击号的不同礼物、用户不能互相吞金额。</summary>
    [Fact]
    public void ComboIdentityIncludesGiftAndSender()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Gift("a", "a", 1, combo: "shared", cumulative: 2));
        accumulator.Add(Gift("b", "b", 2, combo: "shared", cumulative: 3));
        var other = Gift("c", "a", 1, combo: "shared", cumulative: 4);
        other.UserId = "another";
        accumulator.Add(other);
        Assert.Equal(12m, accumulator.Build().PaidRevenue);
    }

    /// <summary>报价和支付即使身份相同，也必须允许支付补齐且只计一次。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardQuoteAndPaymentHaveOnePaidContribution(bool paidFirst)
    {
        var accumulator = new AnalyticsAccumulator();
        var quote = Guard("same", "ProtocolQuote", 198m);
        var paid = Guard("same", "ProtocolPaid", 138m);
        accumulator.Add(paidFirst ? paid : quote);
        accumulator.Add(paidFirst ? quote : paid);
        accumulator.Add(paid);
        Assert.Equal(138m, accumulator.Build().PaidRevenue);
    }

    /// <summary>仅观察到 GUARD_BUY 报价不能宣称发生了已付流水。</summary>
    [Fact]
    public void GuardQuoteAloneNeverBecomesPaidFlow()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Guard("quote", "ProtocolQuote", 198m));
        Assert.Equal(0m, accumulator.Build().PaidRevenue);
    }

    /// <summary>同一秒相同用户的两个真实服务器购买 ID 仍是两笔支付。</summary>
    [Fact]
    public void DifferentGuardServerIdsAtSameTimestampRemainDistinct()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Guard("first", "ProtocolPaid", 138m));
        accumulator.Add(Guard("second", "ProtocolPaid", 138m));
        Assert.Equal(276m, accumulator.Build().PaidRevenue);
    }

    /// <summary>负价、未知币种和缺失价格单列；银瓜子和红包不参与观测金额。</summary>
    [Fact]
    public void UnknownNegativeAndNonMonetaryEventsCannotCreatePaidValue()
    {
        var accumulator = new AnalyticsAccumulator();
        var unknown = Gift("unknown", "gift", 0);
        unknown.Amount = null;
        unknown.PriceSource = "Unknown";
        accumulator.Add(unknown);
        var negative = Gift("negative", "gift", 1);
        negative.Amount = -1;
        accumulator.Add(negative);
        var silver = Gift("silver", "gift", 100);
        silver.CoinType = "silver";
        accumulator.Add(silver);
        var unrecognized = Gift("coin", "gift", 100);
        unrecognized.CoinType = "unrecognized";
        accumulator.Add(unrecognized);
        accumulator.Add(new() { EventId = "red", Kind = "RedPocket", Amount = 1000m });
        var report = accumulator.Build();
        Assert.Equal(0m, report.PaidRevenue);
        Assert.Equal(3, report.UnknownAmountCount);
    }

    /// <summary>同一事件从未知价补齐到协议价格后不再保留未知计数。</summary>
    [Fact]
    public void KnownPriceCanEnrichPreviouslyUnknownComboEvent()
    {
        var accumulator = new AnalyticsAccumulator();
        var unknown = Gift("same-row", "gift", 0, quantity: 2, combo: "combo", cumulative: 2);
        unknown.Amount = null;
        unknown.PriceSource = "Unknown";
        accumulator.Add(unknown);
        accumulator.Add(Gift("same-row", "gift", 0.1m, quantity: 2, combo: "combo", cumulative: 2));
        var report = accumulator.Build();
        Assert.Equal(0.2m, report.PaidRevenue);
        Assert.Equal(0, report.UnknownAmountCount);
    }

    /// <summary>金额全程保留 decimal，不产生二进制浮点累计误差。</summary>
    [Fact]
    public void FractionalGoldValueAccumulatesExactlyAsDecimal()
    {
        var accumulator = new AnalyticsAccumulator();
        for (var index = 0; index < 100; index++)
        {
            accumulator.Add(Gift("decimal-" + index, "gift", 0.1m));
        }
        Assert.Equal(10m, accumulator.Build().PaidRevenue);
    }

    /// <summary>总额、榜单及分钟桶都排除报价，并分别保留金币面值与支付来源。</summary>
    [Fact]
    public void ReportSeparatesQuoteAndPaymentWithoutMutatingPreviousSnapshot()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Gift("gift", "gift", 0.001m));
        accumulator.Add(Guard("guard", "ProtocolQuote", 198m));
        var before = accumulator.Build();
        accumulator.Add(Guard("guard", "ProtocolPaid", 138m));
        var report = accumulator.Build();
        Assert.Equal(0.001m, report.GoldGiftValue);
        Assert.Equal(138m, report.ProtocolPaidAmount);
        Assert.Equal(198m, report.QuotedAmount);
        Assert.Equal(138.001m, report.ObservedAmount);
        Assert.Equal(report.ObservedAmount, report.PaidRevenue);
        Assert.Equal(report.ObservedAmount, Assert.Single(report.TopUsers).PaidAmount);
        Assert.Equal(report.ObservedAmount, Assert.Single(report.Buckets).PaidAmount);
        Assert.Equal(0.001m, before.ObservedAmount);
        Assert.Equal(0.001m, Assert.Single(before.TopUsers).PaidAmount);
    }

    /// <summary>缺失稳定身份的观察独立计值并暴露质量计数，不能把不同礼物默认为同一笔。</summary>
    [Fact]
    public void UnidentifiedFinancialRowsAreFlaggedAndNeverCollapsedByUserAlone()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Gift("", "gift", 1m));
        accumulator.Add(Gift("", "gift", 2m));
        var report = accumulator.Build();
        Assert.Equal(3m, report.ObservedAmount);
        Assert.Equal(2, report.UnidentifiedFinancialEvents);
    }

    internal static RecordedEventEnvelope Gift(string id, string giftId, decimal unitAmount, int quantity = 1, string transaction = "", string combo = "", int cumulative = 0)
    {
        return new()
        {
            EventId = id, Kind = "Gift", UserId = "1", UserName = "user", CoinType = "gold", PriceSource = "ProtocolGold", Quantity = quantity,
            Amount = unitAmount * quantity, TransactionId = transaction,
            Payload = new JsonObject
            {
                ["roomId"] = "42", ["giftId"] = giftId, ["giftName"] = "礼物", ["unitPrice"] = unitAmount * 1000m,
                ["quantity"] = quantity, ["batchComboId"] = combo, ["batchComboCount"] = cumulative, ["transactionId"] = transaction
            }
        };
    }

    internal static RecordedEventEnvelope Guard(string id, string source, decimal? amount)
    {
        var identity = "guard:42:id:" + id;
        return new()
        {
            EventId = identity, Kind = "Guard", Command = source == "ProtocolQuote" ? "GUARD_BUY" : "USER_TOAST_MSG_V2", PriceSource = source,
            UserId = "1", UserName = "user", Amount = amount,
            Payload = new JsonObject { ["identity"] = identity, ["messageId"] = id, ["roomId"] = "42", ["uid"] = 1, ["guardLevel"] = 3, ["timestamp"] = 1800000000, ["quantity"] = 1 }
        };
    }
}
