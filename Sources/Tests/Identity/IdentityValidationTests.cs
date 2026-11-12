using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>注册输入的边界与邮箱一致性测试。</summary>
public sealed class IdentityValidationTests
{
    [Fact]
    public void EmailUsesOneStableCaseInsensitiveKey() => Assert.Equal("person@example.com", IdentityValidation.NormalizeEmail(" Person@Example.COM "));

    [Theory]
    [InlineData("Name <person@example.com>")]
    [InlineData("person@example.com\r\nBcc:other@example.com")]
    [InlineData("not-an-email")]
    public void InvalidEmailsAreRejected(string value) => Assert.Throws<InfrastructureException>(() => IdentityValidation.NormalizeEmail(value));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(257)]
    public void PasswordLengthIsEnforced(int length) => Assert.Throws<InfrastructureException>(() => IdentityValidation.ValidatePassword(new string('a', length)));
}
