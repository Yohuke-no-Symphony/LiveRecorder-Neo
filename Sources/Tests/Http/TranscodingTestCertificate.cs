using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class TranscodingTestCertificate : IDisposable
{
    internal string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "live-recorder-worker-certificates", Guid.NewGuid().ToString("N"));
    internal string Path { get; }
    internal string Sha256 { get; }
    internal int ControlPort { get; }

    internal TranscodingTestCertificate()
    {
        Directory.CreateDirectory(DirectoryPath);
        Path = System.IO.Path.Combine(DirectoryPath, "server.pfx");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("localhost");
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        File.WriteAllBytes(Path, certificate.Export(X509ContentType.Pfx));
        Sha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        ControlPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
