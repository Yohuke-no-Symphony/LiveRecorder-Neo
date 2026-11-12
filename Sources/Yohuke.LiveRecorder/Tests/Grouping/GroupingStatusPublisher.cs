using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Grouping.Tests;

internal sealed class GroupingStatusPublisher : IStatusPublisher
{
    public Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
