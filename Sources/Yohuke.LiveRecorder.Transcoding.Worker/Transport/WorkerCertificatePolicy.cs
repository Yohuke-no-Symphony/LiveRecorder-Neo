using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal static class WorkerCertificatePolicy
{
    internal static bool Validate(WorkerConnectionOptions options, X509Certificate? certificate, SslPolicyErrors errors, bool api = false)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }
        var pin = (api ? options.ApiCertificateSha256 : options.ControlCertificateSha256) ?? options.ServerCertificateSha256;
        if (string.IsNullOrWhiteSpace(pin))
        {
            return errors == SslPolicyErrors.None;
        }
        using var current = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var now = DateTime.UtcNow;
        var actual = SHA256.HashData(current.RawData);
        return now >= current.NotBefore.ToUniversalTime() && now <= current.NotAfter.ToUniversalTime()
            && CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(pin));
    }
}
