using System.Text.Json;
using Xunit;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>通过真实共享协议解析和录播映射验证流水边界。</summary>
public sealed class AnalyticsProtocolTests
{
    /// <summary>V2 同事务中的重复礼物 occurrence 和不同礼物都计值，重放整包不增加数量。</summary>
    [Fact]
    public void V2BatchPreservesOccurrencesAndDeduplicatesPacketReplay()
    {
        const string BATCH = "CCoSBHVzZXJSJggqEgPoirEYAihkQgRnb2xkSgZzaGFyZWRQgKSn2gZiBWNvbWJvUiYIKhID6IqxGAIoZEIEZ29sZEoGc2hhcmVkUICkp9oGYgVjb21ib1InCCsSA+eBrxgDKOgHQgRnb2xkSgZzaGFyZWRQgKSn2gZiBWNvbWJv";
        var raw = JsonSerializer.Serialize(new { cmd = "SEND_GIFT_V2", data = new { pb = BATCH } });
        var source = new BilibiliRawBusinessEvent("42", "SEND_GIFT_V2", raw, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        var accumulator = new AnalyticsAccumulator();
        foreach (var item in BilibiliEventMapper.Map(source).Concat(BilibiliEventMapper.Map(source)))
        {
            accumulator.Add(item);
        }
        var report = accumulator.Build();
        Assert.Equal(3.4m, report.GoldGiftValue);
        Assert.Equal(3, report.GiftCount);
        Assert.Equal(7, report.GiftQuantity);
    }

    /// <summary>旧协议同事务 ID 的不同礼物即使映射事件 ID 相同也应分别计值。</summary>
    [Fact]
    public void LegacyGiftSharedTransactionDifferentGiftIdsAreBothCounted()
    {
        var first = Map("SEND_GIFT", """
            {"cmd":"SEND_GIFT","data":{"uid":42,"uname":"user","giftId":1,"giftName":"花","num":1,"price":3000,"coin_type":"gold","tid":"shared","timestamp":1800000000}}
            """);
        var second = Map("SEND_GIFT", """
            {"cmd":"SEND_GIFT","data":{"uid":42,"uname":"user","giftId":2,"giftName":"灯","num":1,"price":5000,"coin_type":"gold","tid":"shared","timestamp":1800000000}}
            """);
        Assert.Equal(first.EventId, second.EventId);
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(first);
        accumulator.Add(second);
        accumulator.Add(first);
        var report = accumulator.Build();
        Assert.Equal(8m, report.GoldGiftValue);
        Assert.Equal(2, report.GiftCount);
    }

    /// <summary>真实报价和支付包 ID 相同时，支付总价不能再次乘购买数量。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardQuotedAndPaidProtocolPacketsRemainDistinct(bool paidFirst)
    {
        var quote = Map("GUARD_BUY", """
            {"cmd":"GUARD_BUY","data":{"uid":42,"username":"user","guard_level":3,"num":2,"start_time":1800000000,"price":198000,"id":"shared"}}
            """);
        var paid = Map("USER_TOAST_MSG_V2", """
            {"cmd":"USER_TOAST_MSG_V2","data":{"msg_id":"shared","sender_uinfo":{"uid":42,"base":{"name":"user"}},"guard_info":{"guard_level":3,"start_time":1800000000},"pay_info":{"num":2,"price":138000},"option":{"source":1,"is_group":false}}}
            """);
        Assert.Equal("ProtocolQuote", quote.PriceSource);
        Assert.Equal("ProtocolPaid", paid.PriceSource);
        Assert.Equal(quote.EventId, paid.EventId);
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(paidFirst ? paid : quote);
        accumulator.Add(paidFirst ? quote : paid);
        accumulator.Add(paid);
        var report = accumulator.Build();
        Assert.Equal(198m, report.QuotedAmount);
        Assert.Equal(138m, report.ProtocolPaidAmount);
        Assert.Equal(138m, report.ObservedAmount);
    }

    /// <summary>缺失及负价格经录播映射后必须归入未知，而不是按零元或负流水处理。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(",\"price\":-1000")]
    public void MissingOrNegativeLegacyGiftPriceIsUnknown(string price)
    {
        var item = Map("SEND_GIFT", "{\"cmd\":\"SEND_GIFT\",\"data\":{\"uid\":42,\"uname\":\"user\",\"giftId\":1,\"giftName\":\"花\",\"num\":1,\"coin_type\":\"gold\",\"tid\":\"shared\"" + price + "}}");
        Assert.Equal("Gift", item.Kind);
        Assert.Equal("Unknown", item.PriceSource);
        Assert.Null(item.Amount);
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(item);
        var report = accumulator.Build();
        Assert.Equal(0m, report.ObservedAmount);
        Assert.Equal(1, report.UnknownAmountCount);
        Assert.Equal(1, report.UnknownGiftQuantity);
    }

    private static RecordedEventEnvelope Map(string command, string raw) => Assert.Single(BilibiliEventMapper.Map(
        new BilibiliRawBusinessEvent("42", command, raw, DateTimeOffset.FromUnixTimeSeconds(1800000000))));
}
