using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class JobTestPublisher : IStatusPublisher
{
    public Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
