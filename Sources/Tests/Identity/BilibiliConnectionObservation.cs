using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed record BilibiliConnectionObservation(long RoomId, string Cookie, BilibiliDanmuReconciliation Reconciliation, CancellationToken CredentialsChanged)
{
    internal TaskCompletionSource<Exception?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Fail(Exception exception) => Completion.TrySetResult(exception);
}
