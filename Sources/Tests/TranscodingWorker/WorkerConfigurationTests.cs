using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证连接信任策略与主站共享的运行配置边界。</summary>
public sealed class WorkerConfigurationTests
{
    /// <summary>连接只能使用 HTTPS，证书固定也不能忽略主机名。</summary>
    [Fact]
    public void ConnectionRequiresHttpsAndNeverTrustsWrongPinnedCertificate()
    {
        var options = new WorkerConnectionOptions { ApiBaseUrl = "http://localhost", WorkerId = "worker", Secret = "secret" };
        Assert.Throws<InvalidDataException>(options.Validate);
        options.ApiBaseUrl = "https://localhost";
        options.Validate();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(WorkerCertificatePolicy.Validate(options, certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        options.ServerCertificateSha256 = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        Assert.True(WorkerCertificatePolicy.Validate(options, certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerCertificatePolicy.Validate(options, certificate, SslPolicyErrors.RemoteCertificateNameMismatch));
        options.ServerCertificateSha256 = new string('0', 64);
        Assert.False(WorkerCertificatePolicy.Validate(options, certificate, SslPolicyErrors.None));
    }

    /// <summary>资源限额遵守共享策略，临时路径必须可明确定位。</summary>
    [Fact]
    public void RuntimeConfigurationRequiresSafeResourceLimits()
    {
        var settings = new WorkerConfiguration { TempDirectory = Path.GetTempPath() };
        WorkerSetup.Validate(settings);
        WorkerSetup.Validate(settings with { MaxConcurrentEncodes = 64, CpuThreadFraction = 1, CpuConcurrency = 64 });
        Assert.Throws<ArgumentException>(() => WorkerSetup.Validate(settings with { CpuThreadFraction = 1.01 }));
        Assert.Throws<ArgumentException>(() => WorkerSetup.Validate(settings with { CpuConcurrency = 65 }));
        Assert.Throws<InvalidDataException>(() => WorkerSetup.Validate(settings with { TempDirectory = "relative" }));
    }
}
