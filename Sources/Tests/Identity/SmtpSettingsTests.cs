using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证 SMTP 吊销检查配置的兼容性与持久化。</summary>
public sealed class SmtpSettingsTests
{
    [Fact]
    public void LegacySettingsKeepCertificateRevocationChecksEnabled()
    {
        var settings = JsonSerializer.Deserialize<ServerSettings>("""{"smtp":{"host":"smtp.example.test"}}""", JsonFiles.Options)!;

        Assert.True(settings.Smtp.CheckCertificateRevocation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CertificateRevocationPreferenceSurvivesSettingsRoundTrip(bool checkCertificateRevocation)
    {
        var settings = new ServerSettings
        {
            Smtp = new() { CheckCertificateRevocation = checkCertificateRevocation }
        };

        var json = JsonSerializer.Serialize(settings, JsonFiles.Options);
        var restored = JsonSerializer.Deserialize<ServerSettings>(json, JsonFiles.Options)!;

        Assert.Equal(checkCertificateRevocation, restored.Smtp.CheckCertificateRevocation);
    }
}
