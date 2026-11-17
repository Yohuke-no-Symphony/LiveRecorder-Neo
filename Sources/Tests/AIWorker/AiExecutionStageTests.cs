using Xunit;
using Yohuke.LiveRecorder.AI.Worker.Execution;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.Application.AI;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

public sealed class AiExecutionStageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-stages-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecoveryAccumulatesOnlyActiveStageTimeAndFinalResultCarriesStages()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        store.Update(value =>
        {
            var previous = value.Stages.Single(stage => stage.Id == "Analyzing");
            previous.State = "Running";
            previous.ElapsedMs = 100;
            previous.StartedAtUtc = DateTime.UtcNow.AddDays(-1);
        });
        var clock = new AiTestTimeProvider();
        var running = new AiRunningJob(store, default, clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        running.Stage = "Analyzing";
        clock.Advance(TimeSpan.FromSeconds(5));
        var heartbeat = running.Snapshot();
        Assert.Equal(5100, heartbeat.Stages.Single(stage => stage.Id == "Analyzing").ElapsedMs);
        running.FinishStage("Completed", null);
        running.Stage = "Reporting";
        running.RefreshStages();
        var transfer = new AiTestTransport();
        running.Lease.Renew(90);

        await new AiRemoteCheckpoint(store, running.Lease, transfer).CompleteAsync(AiExecutionMapper.ToJob(store.Read().Execution), null, default);

        var stages = Assert.Single(transfer.Results).Stages;
        Assert.Equal(5100, stages.Single(stage => stage.Id == "Analyzing").ElapsedMs);
        Assert.Equal("Skipped", stages.Single(stage => stage.Id == "Preparing").State);
        Assert.Equal("Completed", stages.Single(stage => stage.Id == "Reporting").State);
        Assert.NotNull(stages.Single(stage => stage.Id == "Reporting").FinishedAtUtc);
        running.Cancellation.Dispose();
    }

    [Fact]
    public void LeaseExpiryUsesMonotonicTimeAndReportOnlyDoesNotBecomeWritableOnRenewal()
    {
        var clock = new AiTestTimeProvider();
        var lease = new AiExecutionLease(clock);
        lease.Renew(5);
        clock.Advance(TimeSpan.FromSeconds(6));
        clock.UtcNow -= TimeSpan.FromHours(1);
        Assert.Throws<AiExecutionPausedException>(lease.RequireModelCall);

        lease.Renew(90, true);
        lease.Renew(90);

        Assert.True(lease.CanReport);
        Assert.True(lease.ReportOnly);
        Assert.Throws<AiExecutionPausedException>(lease.RequireModelCall);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
