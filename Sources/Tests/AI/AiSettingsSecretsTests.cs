using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiSettingsSecretsTests
{
    [Fact]
    public void KeysAreEncryptedRedactedRetainedAndExplicitlyDeleted()
    {
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var protectedSettings = AiSettingsSecrets.Protect(new() { ApiKey = "sensitive" }, new(), protector);
        Assert.NotEqual("sensitive", protectedSettings.ApiKey);
        Assert.Equal("sensitive", protector.Unprotect(protectedSettings.ApiKey));
        var publicSettings = AiSettingsSecrets.Redact(protectedSettings);
        Assert.Equal("", publicSettings.ApiKey);
        Assert.True(publicSettings.HasApiKey);
        var retained = AiSettingsSecrets.Protect(publicSettings, protectedSettings, protector);
        Assert.Equal(protectedSettings.ApiKey, retained.ApiKey);
        var cleared = AiSettingsSecrets.Protect(new() { ClearApiKey = true, ApiKey = "ignored" }, retained, protector);
        Assert.Equal("", cleared.ApiKey);
        Assert.False(cleared.ClearApiKey);
        Assert.Equal(protectedSettings.ApiKey, retained.ApiKey);
    }

    [Theory]
    [InlineData("https://user:key@example.test/v1")]
    [InlineData("https://example.test/v1?key=secret")]
    [InlineData("file:///tmp/model")]
    public void CredentialsInEndpointsAreRejected(string url) => Assert.Throws<InfrastructureException>(() => AiAnalysisPolicy.Endpoint(url));

    [Fact]
    public void DisabledLegacySettingsRemainValidWithoutModelOrKey() => AiAnalysisPolicy.Validate(new AiSettings());
}
