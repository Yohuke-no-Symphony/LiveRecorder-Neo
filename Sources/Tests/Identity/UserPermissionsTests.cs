using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证权限声明、旧用户兼容和注册时的默认权限快照。</summary>
public sealed class UserPermissionsTests
{
    /// <summary>旧数据库账号未保存权限字段时不自动获得任何新增权限。</summary>
    [Fact]
    public void LegacyAccountWithoutPermissionFieldKeepsAllOptionalAbilitiesDisabled()
    {
        var document = new UserAccount { Email = "legacy@example.test" }.ToBsonDocument();
        document.Remove(nameof(UserAccount.Permissions));
        var user = BsonSerializer.Deserialize<UserAccount>(document);
        Assert.Equal(new UserPermissions(), user.Permissions);
    }

    /// <summary>三个权限独立转换为声明，不互相隐含授予。</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void PrincipalContainsExactlyTheAssignedPermissionClaims(bool manageRecordings, bool viewAnalytics, bool purgeRecordings)
    {
        var profile = new UserProfile("viewer", "viewer@example.test", "用户", "", "User", "Active")
        {
            Permissions = new() { ManageRecordings = manageRecordings, ViewAnalytics = viewAnalytics, PurgeRecordings = purgeRecordings }
        };
        var principal = UserPrincipalFactory.Create(profile);
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(profile.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.False(principal.IsInRole("Admin"));
        Assert.Equal(manageRecordings, principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.MANAGE_RECORDINGS));
        Assert.Equal(viewAnalytics, principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.VIEW_ANALYTICS));
        Assert.Equal(purgeRecordings, principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.PURGE_RECORDINGS));
        Assert.Equal(new[] { manageRecordings, viewAnalytics, purgeRecordings }.Count(value => value), principal.FindAll(UserPermissionNames.CLAIM_TYPE).Count());
    }

    /// <summary>管理员即使没有显式权限快照也具备全部能力。</summary>
    [Fact]
    public void AdministratorPrincipalHasEveryPermissionWithoutExplicitGrants()
    {
        var principal = UserPrincipalFactory.Create(new("administrator", "admin@example.test", "管理员", "", "Admin", "Active"));
        Assert.True(principal.IsInRole("Admin"));
        Assert.True(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.MANAGE_RECORDINGS));
        Assert.True(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.VIEW_ANALYTICS));
        Assert.True(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.PURGE_RECORDINGS));
    }

    /// <summary>默认权限只复制到新注册账号，后续修改默认值不会改变已有用户。</summary>
    [MongoFact]
    public async Task NewRegistrationsSnapshotDefaultsWithoutChangingExistingUsers()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var firstDefaults = new UserPermissions { ManageRecordings = true, ViewAnalytics = true };
        var settings = await fixture.Settings.GetAsync();
        settings.DefaultUserPermissions = firstDefaults;
        await fixture.Settings.SaveAsync(settings);
        var first = await RegisterAsync(fixture, "first-permissions@example.test");
        Assert.Equal(firstDefaults, first.Permissions);
        Assert.Equal("Pending", first.Status);

        var secondDefaults = new UserPermissions { PurgeRecordings = true };
        settings.DefaultUserPermissions = secondDefaults;
        await fixture.Settings.SaveAsync(settings);
        var second = await RegisterAsync(fixture, "second-permissions@example.test");
        Assert.Equal(secondDefaults, second.Permissions);
        Assert.Equal(firstDefaults, (await fixture.Identity.GetUserAsync(first.Id))!.Permissions);
        Assert.Equal(firstDefaults, (await fixture.Context.Collection<UserAccount>("users").Find(user => user.Id == first.Id).SingleAsync()).Permissions);
    }

    /// <summary>修改用户权限不影响审批和角色，升为管理员后全权，降级后恢复分配快照。</summary>
    [MongoFact]
    public async Task AssignedPermissionsSurviveRoleChangesAndDoNotAlterApproval()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var user = await RegisterAsync(fixture, "role-permissions@example.test");
        var assigned = new UserPermissions { ManageRecordings = true };
        var updated = await fixture.Identity.SetPermissionsAsync(user.Id, assigned);
        Assert.Equal(assigned, updated.Permissions);
        Assert.Equal(user.Status, updated.Status);
        Assert.Equal("User", updated.Role);
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        await fixture.Identity.SetApprovalAsync(user.Id, true);

        var administrator = await fixture.Identity.SetRoleAsync(user.Id, "Admin");
        Assert.Equal(new UserPermissions { ManageRecordings = true, ViewAnalytics = true, PurgeRecordings = true }, administrator.Permissions);
        Assert.Equal(assigned, (await fixture.Context.Collection<UserAccount>("users").Find(account => account.Id == user.Id).SingleAsync()).Permissions);
        var demoted = await fixture.Identity.SetRoleAsync(user.Id, "User");
        Assert.Equal(assigned, demoted.Permissions);
        var missing = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetPermissionsAsync("missing-user", assigned));
        Assert.Equal(404, missing.StatusCode);
    }

    /// <summary>OAuth 完成新账号注册时同样复制管理员当前设置的默认权限。</summary>
    [MongoFact]
    public async Task OAuthRegistrationUsesTheSameDefaultPermissionSnapshot()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var defaults = new UserPermissions { ViewAnalytics = true, PurgeRecordings = true };
        var settings = await fixture.Settings.GetAsync();
        settings.DefaultUserPermissions = defaults;
        await fixture.Settings.SaveAsync(settings);
        var ticket = Guid.NewGuid().ToString("N");
        var email = "oauth-permissions@example.test";
        await fixture.Context.Collection<OAuthRegistration>("oauth_registrations").InsertOneAsync(new()
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))),
            ProviderId = "permissions-test",
            Subject = "external-permissions-user",
            Email = email,
            DisplayName = "外部用户",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        });
        var invitation = await fixture.Identity.CreateInvitationAsync(new(), "admin");
        var code = await fixture.SendCodeAsync(email);
        var user = await fixture.Identity.CompleteOAuthRegistrationAsync(ticket, new(email, "外部用户", code, invitation.Code!));
        Assert.Equal(defaults, user.Permissions);
        Assert.Equal("Pending", user.Status);
        Assert.Equal(defaults, (await fixture.Identity.GetUserAsync(user.Id))!.Permissions);
    }

    private static async Task<UserProfile> RegisterAsync(IdentityFixture fixture, string email)
    {
        var invitation = await fixture.Identity.CreateInvitationAsync(new(), "admin");
        var code = await fixture.SendCodeAsync(email);
        return await fixture.Identity.RegisterAsync(new(email, IdentityFixture.USER_PASSWORD, "用户", code, invitation.Code!));
    }
}
