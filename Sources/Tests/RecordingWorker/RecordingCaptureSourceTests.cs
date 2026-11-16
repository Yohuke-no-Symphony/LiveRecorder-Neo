using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies the production Bilibili source preserves event reconciliation when a room's subscription is replaced.</summary>
public sealed class RecordingCaptureSourceTests
{
    /// <summary>History replay after a configuration revision cannot repeat a stable message accepted by the prior subscription.</summary>
    [Fact]
    public async Task ResubscriptionRetainsTheRoomsDanmuReconciliation()
    {
        using var fixture = new RecordingWorkerFixture();
        var connection = new RecordingReconciliationTestConnection();
        var source = new BilibiliRecordingCaptureSource(connection);
        var received = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            var subscription = source.SubscribeEventsAsync(fixture.Assignment, (_, _) =>
            {
                Interlocked.Increment(ref received);
                return Task.CompletedTask;
            }, cancellation.Token);
            await connection.NextAsync();
            await cancellation.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var subscription = source.SubscribeEventsAsync(fixture.Assignment with { ConfigurationRevision = 2 }, (_, _) =>
            {
                Interlocked.Increment(ref received);
                return Task.CompletedTask;
            }, cancellation.Token);
            await connection.NextAsync();
            await cancellation.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, received);
    }
}
