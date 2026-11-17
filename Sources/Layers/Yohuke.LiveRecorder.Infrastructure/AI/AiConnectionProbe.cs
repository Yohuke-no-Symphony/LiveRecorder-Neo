using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

internal sealed class AiConnectionProbe
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal AiSettings Settings { get; init; } = new();
    internal string ApiKey { get; set; } = "";
    internal DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    internal string? WorkerId { get; set; }
    internal string LeaseToken { get; set; } = "";
    internal DateTime LeaseUntilUtc { get; set; }
    internal long Sequence { get; set; }
    internal string? CheckpointHash { get; set; }
    internal string? CompletionHash { get; set; }
    internal TaskCompletionSource<AiConnectionTestResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal AiAssignment Assignment => new()
    {
        JobId = Id, Attempt = 1, LeaseToken = LeaseToken, IsConnectionTest = true,
        ConfigurationFingerprint = AiConnectionTestCoordinator.ConfigurationFingerprint(Settings)
    };
}
