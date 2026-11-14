using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed record BilibiliConnectionObservation(long RoomId, string Cookie, BilibiliDanmuReconciliation Reconciliation, CancellationToken CredentialsChanged);
