using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestStatusPublisher : IStatusPublisher
{
    internal ConcurrentQueue<(string Kind, string Id, string? Owner)> Events { get; } = new();

    public Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        Events.Enqueue((kind, id, ownerId));
        return Task.CompletedTask;
    }
}
