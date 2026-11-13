using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>通过回环 SMTP 服务验证实际邮件发送。</summary>
public sealed class SmtpIntegrationTests
{
    [Fact]
    public async Task MailKitSendsVerificationMailToConfiguredServer()
    {
        await using var server = new SmtpCaptureServer();
        var settings = new StaticIdentitySettingsStore(new ServerSettings { Smtp = new() { Host = "127.0.0.1", Port = server.Port, FromAddress = "recorder@example.test", FromName = "Recorder" } });
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var sender = new SmtpEmailSender(settings, protector);
        await sender.SendAsync("viewer@example.test", "Verification", "Code: 123456");
        var message = await server.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("viewer@example.test", message);
        Assert.Contains("recorder@example.test", message);
        Assert.Contains("123456", message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UntrustedServerCertificateIsRejectedRegardlessOfRevocationSetting(bool checkCertificateRevocation)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        await using var server = new SmtpCaptureServer(certificate);
        var settings = new StaticIdentitySettingsStore(new ServerSettings
        {
            Smtp = new()
            {
                Host = "localhost",
                Port = server.Port,
                UseSsl = true,
                CheckCertificateRevocation = checkCertificateRevocation,
                FromAddress = "recorder@example.test"
            }
        });
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var sender = new SmtpEmailSender(settings, protector);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<SslHandshakeException>(() => sender.SendAsync("viewer@example.test", "Verification", "Code: 123456", cancellation.Token));
        Assert.False(server.Delivered.Task.IsCompleted);
    }
}
