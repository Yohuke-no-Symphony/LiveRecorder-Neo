using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class CallbackRecordingPublisher(Func<string, string, CancellationToken, Task> publish) : IStatusPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        return publish(kind, id, cancellationToken);
    }
}
