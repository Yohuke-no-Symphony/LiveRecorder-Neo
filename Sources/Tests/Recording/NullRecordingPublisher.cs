using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class NullRecordingPublisher : IStatusPublisher
{
    public Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
