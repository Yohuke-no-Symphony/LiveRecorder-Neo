using System.Security.Cryptography;
using System.Text;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal static class IdentityToken
{
    internal static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool EqualsHash(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    internal static string CodeHash(string email, string code, string salt) => Hash($"{email}\n{code}\n{salt}");
}
