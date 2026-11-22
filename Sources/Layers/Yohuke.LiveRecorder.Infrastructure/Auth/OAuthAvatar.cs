namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal sealed record OAuthAvatar(string Id, string ContentType, byte[] Content, string ETag);
