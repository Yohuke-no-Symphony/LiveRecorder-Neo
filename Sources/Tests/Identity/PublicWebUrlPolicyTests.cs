using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>限制密码恢复邮件的可信地址，防止请求来源影响链接。</summary>
public sealed class PublicWebUrlPolicyTests
{
    [Theory]
    [InlineData("https://recorder.example.test", "https://recorder.example.test")]
    [InlineData(" https://recorder.example.test/ ", "https://recorder.example.test")]
    [InlineData("https://recorder.example.test:8443/", "https://recorder.example.test:8443")]
    [InlineData("http://localhost:5784/", "http://localhost:5784")]
    [InlineData("http://127.0.0.1:5784", "http://127.0.0.1:5784")]
    [InlineData("http://[::1]:5784", "http://[::1]:5784")]
    public void HttpsOriginsAndLoopbackDevelopmentAddressesAreNormalized(string input, string expected)
    {
        Assert.Equal(expected, PublicWebUrlPolicy.Normalize(input));
        Assert.True(PublicWebUrlPolicy.TryGetBaseUri(input, out var uri));
        Assert.Equal(expected + "/", uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://recorder.example.test")]
    [InlineData("http://192.168.1.2")]
    [InlineData("https://user:secret@recorder.example.test")]
    [InlineData("https://recorder.example.test/subpath")]
    [InlineData("https://recorder.example.test/?target=elsewhere")]
    [InlineData("https://recorder.example.test/#fragment")]
    [InlineData("//recorder.example.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///tmp/")]
    [InlineData("https://recorder.example.test\\evil")]
    [InlineData("https://recorder.example.test/\nother")]
    public void UnsafeOrUnsupportedWebsiteAddressesAreRejected(string input)
    {
        Assert.False(PublicWebUrlPolicy.TryGetBaseUri(input, out _));
        Assert.Equal(MessageCode.AUTH_PASSWORD_RESET_PUBLIC_URL_INVALID.ToString(),
            Assert.Throws<InfrastructureException>(() => PublicWebUrlPolicy.Normalize(input)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UnconfiguredOriginsRemainOptional(string? input)
    {
        Assert.Equal("", PublicWebUrlPolicy.Normalize(input));
        Assert.False(PublicWebUrlPolicy.TryGetBaseUri(input, out _));
    }
}
