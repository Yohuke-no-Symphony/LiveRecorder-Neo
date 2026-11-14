namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed record BilibiliAccountFailure(string ProtectedCredentials, DateTime FailedAtUtc);
