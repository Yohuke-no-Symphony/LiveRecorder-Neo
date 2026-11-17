namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed class AudienceAvatarCacheEntry(long userId)
{
    internal TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LinkedListNode<long> RecencyNode { get; } = new(userId);
    internal DateTimeOffset ExpiresAt { get; set; }
}
