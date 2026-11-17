using Yohuke.LiveRecorder.Transcoding.Worker.Services;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>Checks repeatable Windows recovery configuration and failure propagation before installation succeeds.</summary>
public sealed class WindowsWorkerRecoveryPolicyTests
{
    /// <summary>Reapplying installation repairs both crash and non-crash recovery without changing service startup mode.</summary>
    [Fact]
    public async Task ReapplyingPolicyConfiguresBothRecoveryModes()
    {
        var calls = new List<IReadOnlyList<string>>();
        var policy = new WindowsWorkerRecoveryPolicy(WorkerServiceIdentity.Recording.ServiceName);
        Task Execute(IReadOnlyList<string> arguments)
        {
            calls.Add(arguments);
            return Task.CompletedTask;
        }

        await policy.ApplyAsync(Execute);
        await policy.ApplyAsync(Execute);

        Assert.Equal(4, calls.Count);
        Assert.Equal(calls[0], calls[2]);
        Assert.Equal(calls[1], calls[3]);
        Assert.Equal("failure", calls[0][0]);
        Assert.Equal(["failureflag", WorkerServiceIdentity.Recording.ServiceName, "1"], calls[1]);
        Assert.All(calls, call => Assert.DoesNotContain("start=", call));
    }

    /// <summary>A failed native policy operation cannot be treated as a successfully configured service.</summary>
    [Fact]
    public async Task RecoveryConfigurationFailureIsPropagated()
    {
        var calls = 0;
        var failure = new IOException("SCM rejected recovery policy");

        var actual = await Assert.ThrowsAsync<IOException>(() => new WindowsWorkerRecoveryPolicy("worker").ApplyAsync(_ =>
        {
            calls++;
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.Equal(1, calls);
    }
}
