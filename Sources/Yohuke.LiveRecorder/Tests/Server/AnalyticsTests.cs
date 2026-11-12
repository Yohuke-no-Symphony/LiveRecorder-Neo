using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;

namespace Yohuke.LiveRecorder.Tests;

public sealed class AnalyticsTests
{
    [Fact]
    public void PaidCombosDeduplicateAndSilverAndRedPocketsAreExcluded()
    {
        var analytics = new AnalyticsAccumulator();
        analytics.Add(new RecordedEventEnvelope { Kind = "Gift", CoinType = "gold", Amount = 10, TransactionId = "combo", UserId = "1" });
        analytics.Add(new RecordedEventEnvelope { Kind = "Gift", CoinType = "gold", Amount = 20, TransactionId = "combo", UserId = "1" });
        analytics.Add(new RecordedEventEnvelope { Kind = "Gift", CoinType = "silver", Amount = 100, UserId = "2" });
        analytics.Add(new RecordedEventEnvelope { Kind = "RedPocket", Amount = 100, TransactionId = "pocket", UserId = "3" });
        analytics.Add(new RecordedEventEnvelope { Kind = "RedPocket", TransactionId = "pocket", UserId = "3" });
        analytics.Add(new RecordedEventEnvelope { Kind = "Guard", Amount = null, UserId = "0" });
        var report = analytics.Build();
        Assert.Equal(20, report.PaidRevenue);
        Assert.Equal(3, report.ActiveUsers);
        Assert.Equal(1, report.RedPocketCount);
        Assert.Equal(1, report.UnknownAmountCount);
    }
}
