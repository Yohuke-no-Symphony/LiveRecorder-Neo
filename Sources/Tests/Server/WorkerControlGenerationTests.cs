using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Verifies that control replacement and heartbeat mutations share one authenticated Worker identity fence.</summary>
public sealed class WorkerControlGenerationTests
{
    /// <summary>An old heartbeat waiting behind replacement cannot update telemetry, renew a lease or claim another task.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public async Task QueuedOldHeartbeatCannotMutateStateAfterReplacement(string kind)
    {
        await using var fixture = await WorkerControlGenerationTestContext.CreateAsync(kind);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 1;
        var before = await fixture.SnapshotAsync();
        var replacement = Task.Run(() => fixture.Workers.RegisterControlConnectionAsync(fixture.WorkerId, fixture.Secret, () =>
        {
            Volatile.Write(ref generation, 2);
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The replacement registration was not released.");
            }
            return Guid.NewGuid();
        }, timeout.Token));
        Task<WorkerState>? stale = null;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            stale = fixture.HeartbeatAsync(() => Volatile.Read(ref generation) == 1, timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(stale.IsCompleted);
            release.Set();
            await replacement.WaitAsync(timeout.Token);
            var rejected = await Assert.ThrowsAsync<InfrastructureException>(() => stale);
            Assert.Equal(409, rejected.StatusCode);
            Assert.Equal(before, await fixture.SnapshotAsync());

            AssertDispatch(await fixture.HeartbeatAsync(() => Volatile.Read(ref generation) == 2, timeout.Token), kind);
        }
        finally
        {
            release.Set();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            if (stale is not null)
            {
                try
                {
                    await stale.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (InfrastructureException)
                {
                }
            }
        }
    }

    /// <summary>A replacement cannot pass the generation check while a validated heartbeat is still mutating state.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public async Task ReplacementWaitsForValidatedHeartbeatToFinish(string kind)
    {
        await using var fixture = await WorkerControlGenerationTestContext.CreateAsync(kind);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = Task.Run(() => fixture.HeartbeatAsync(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The validated heartbeat was not released.");
            }
            return true;
        }, timeout.Token));
        Task<Guid>? replacement = null;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            replacement = fixture.Workers.RegisterControlConnectionAsync(fixture.WorkerId, fixture.Secret, () =>
            {
                registered.SetResult();
                return Guid.NewGuid();
            }, timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(registered.Task.IsCompleted);
            Assert.False(replacement.IsCompleted);
            release.Set();
            AssertDispatch(await heartbeat.WaitAsync(timeout.Token), kind);
            await replacement.WaitAsync(timeout.Token);
            Assert.True(registered.Task.IsCompletedSuccessfully);
        }
        finally
        {
            release.Set();
            await heartbeat.WaitAsync(TimeSpan.FromSeconds(5));
            if (replacement is not null)
            {
                await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>The registration callback cannot execute using credentials revoked after the initial handshake.</summary>
    [Fact]
    public async Task RegistrationRevalidatesTheConnectionCredential()
    {
        await using var fixture = await WorkerControlGenerationTestContext.CreateAsync("Recording");
        Assert.True(await fixture.Workers.AuthenticateAsync(fixture.WorkerId, fixture.Secret, CancellationToken.None));
        await fixture.Workers.RotateAsync(fixture.WorkerId, CancellationToken.None);
        var registered = false;

        var rejected = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Workers.RegisterControlConnectionAsync(
            fixture.WorkerId, fixture.Secret, () =>
            {
                registered = true;
                return Guid.NewGuid();
            }));

        Assert.Equal(401, rejected.StatusCode);
        Assert.False(registered);
        Assert.Single(await fixture.Workers.ListAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.Jobs.Mongo.Collection<RecordingOwnership>("recordingOwnerships")
            .CountDocumentsAsync(value => value.OwnerId == fixture.WorkerId && value.State == "Owned"));
    }

    private static void AssertDispatch(WorkerState state, string kind)
    {
        if (kind == "Recording")
        {
            Assert.Equal(2, state.RecordingAssignments.Count);
            return;
        }
        Assert.True(Assert.Single(state.Leases).Accepted);
        Assert.Single(kind == "Transcoding" ? state.Assignments.Select(value => value.JobId) : state.ClipAssignments.Select(value => value.JobId));
    }
}
