using System.Security.Cryptography;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal sealed record UserAvatarImage(string ContentType, byte[] Content, string ETag)
{
    internal const int MAX_IMAGE_BYTES = 2 * 1024 * 1024;

    internal static UserAvatarImage? FromBytes(byte[] content)
    {
        if (content.Length is < 12 or > MAX_IMAGE_BYTES || GetContentType(content) is not { } contentType)
        {
            return null;
        }
        return new(contentType, content, $"\"{Convert.ToHexString(SHA256.HashData(content))}\"");
    }

    private static string? GetContentType(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith<byte>([0xff, 0xd8, 0xff]))
        {
            return "image/jpeg";
        }
        if (content.StartsWith<byte>([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
        {
            return "image/png";
        }
        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        return content.StartsWith("RIFF"u8) && content[8..].StartsWith("WEBP"u8) ? "image/webp" : null;
    }
}
