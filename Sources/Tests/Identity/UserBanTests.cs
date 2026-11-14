using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证封禁状态、会话撤销和旧用户安全版本兼容。</summary>
public sealed class UserBanTests
{
    /// <summary>缺失声明视为旧版本零，损坏声明不会降级为旧会话。</summary>
    [Theory]
    [InlineData(null, 0L)]
    [InlineData("0", 0L)]
    [InlineData("17", 17L)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    [InlineData("invalid", null)]
    [InlineData("9223372036854775808", null)]
    public void CookieVersionRejectsMalformedClaims(string? value, long? expected)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user") };
        if (value is not null)
        {
            claims.Add(new(UserPrincipalFactory.SESSION_VERSION_CLAIM, value));
        }
        Assert.Equal(expected, UserPrincipalFactory.GetSessionVersion(new(new ClaimsIdentity(claims, "Cookies"))));
    }

    /// <summary>安全版本进入 Cookie，不通过用户资料 JSON 暴露。</summary>
    [Fact]
    public void SecurityVersionFlowsThroughPrincipalButNotProfileJson()
    {
        var profile = new UserProfile("user", "user@example.test", "用户", "", "User", "Active") { SessionVersion = 3 };
        Assert.Equal(3, UserPrincipalFactory.GetSessionVersion(UserPrincipalFactory.Create(profile)));
        Assert.DoesNotContain("SessionVersion", JsonSerializer.Serialize(profile));
    }

    /// <summary>解封保留安全版本，旧会话不能恢复，资料和权限保持不变。</summary>
    [MongoFact]
    public async Task UnbanDoesNotResurrectOldSessionsOrChangeAccountData()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var admin = Assert.Single(await fixture.Identity.ListUsersAsync());
        var user = await CreateUserAsync(fixture);
        Assert.NotNull(await fixture.Identity.ValidateSessionAsync(user.Id));
        var banned = await fixture.Identity.SetBannedAsync(admin.Id, user.Id, true);
        Assert.Equal("Disabled", banned.Status);
        Assert.Equal(1, banned.SessionVersion);
        Assert.Equal((user.Id, 1L), Assert.Single(fixture.SessionRevoker.Calls));
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id, 1));
        Assert.Equal(403, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.AuthenticateAsync(new(user.Email, IdentityFixture.USER_PASSWORD)))).StatusCode);
        var approval = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetApprovalAsync(user.Id, true));
        Assert.Equal(nameof(MessageCode.AUTH_USER_STATUS_CONFLICT), approval.Code);
        await fixture.Identity.SetBannedAsync(admin.Id, user.Id, true);
        Assert.Single(fixture.SessionRevoker.Calls);
        var restored = await fixture.Identity.SetBannedAsync(admin.Id, user.Id, false);
        Assert.Equal("Active", restored.Status);
        Assert.Equal(1, restored.SessionVersion);
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        Assert.NotNull(await fixture.Identity.ValidateSessionAsync(user.Id, restored.SessionVersion));
        var login = await fixture.Identity.AuthenticateAsync(new(user.Email, IdentityFixture.USER_PASSWORD));
        Assert.Equal(1, login.SessionVersion);
        var stored = await fixture.Context.Collection<UserAccount>("users").Find(item => item.Id == user.Id).SingleAsync();
        Assert.Equal(user.Email, stored.Email);
        Assert.Equal(user.DisplayName, stored.DisplayName);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.Equal(user.Permissions, stored.Permissions);
    }

    /// <summary>封禁仅允许有效管理员操作有效用户，不能自封。</summary>
    [MongoFact]
    public async Task BanEnforcesActorAndApprovalBoundaries()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var admin = Assert.Single(await fixture.Identity.ListUsersAsync());
        var user = await CreateUserAsync(fixture);
        Assert.Equal(nameof(MessageCode.AUTH_SELF_BAN_FORBIDDEN),
            (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetBannedAsync(admin.Id, admin.Id, true))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetBannedAsync(user.Id, admin.Id, true))).StatusCode);
        foreach (var status in new[] { "Pending", "Rejected" })
        {
            await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(item => item.Id == user.Id, Builders<UserAccount>.Update.Set(item => item.Status, status));
            Assert.Equal(409, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetBannedAsync(admin.Id, user.Id, true))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetBannedAsync(admin.Id, user.Id, false))).StatusCode);
        }
        Assert.Equal("Admin", (await fixture.Identity.ValidateSessionAsync(admin.Id))!.Role);
        Assert.Empty(fixture.SessionRevoker.Calls);
    }

    /// <summary>审核撤销后重新批准同样不能恢复旧 Cookie。</summary>
    [MongoFact]
    public async Task ReapprovalPreservesRevocationAndRoleUpdatesPreserveCredentials()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var user = await CreateUserAsync(fixture);
        await fixture.Identity.SetApprovalAsync(user.Id, false);
        await fixture.Identity.SetApprovalAsync(user.Id, true);
        await fixture.Identity.SetRoleAsync(user.Id, "Admin");
        var stored = await fixture.Context.Collection<UserAccount>("users").Find(item => item.Id == user.Id).SingleAsync();
        Assert.Equal(1, stored.SessionVersion);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        Assert.NotNull(await fixture.Identity.ValidateSessionAsync(user.Id, 1));
    }

    private static async Task<UserAccount> CreateUserAsync(IdentityFixture fixture)
    {
        var user = new UserAccount { Email = "ban-user@example.test", NormalizedEmail = "ban-user@example.test", DisplayName = "用户资料", Status = "Active" };
        user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, IdentityFixture.USER_PASSWORD);
        await fixture.Context.Collection<UserAccount>("users").InsertOneAsync(user);
        return user;
    }
}
