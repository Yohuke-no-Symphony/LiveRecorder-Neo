using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Xunit;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Auth;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证个人同传配置迁移和新权限的最小授权边界。</summary>
public sealed class RecordingConfigurationCompatibilityTests
{
    /// <summary>旧个人字段可兼容读取，重新序列化不再输出且不改变权限。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("^旧个人规则")]
    public void LegacyPersonalPatternIsIgnoredOnlyForUserAccounts(string? pattern)
    {
        MongoSerialization.EnsureInitialized();
        var document = new BsonDocument
        {
            { "TranslationDanmuPattern", pattern is null ? BsonNull.Value : new BsonString(pattern) },
            { "Permissions", new BsonArray { PermissionIds.MANAGE_RECORDINGS } }
        };
        var account = BsonSerializer.Deserialize<UserAccount>(document);
        Assert.False(account.ToBsonDocument().Contains("TranslationDanmuPattern"));
        Assert.Equal(PermissionIds.MANAGE_RECORDINGS, Assert.Single(account.Permissions));
        Assert.False(BsonClassMap.LookupClassMap(typeof(BroadcastSession)).IgnoreExtraElements);
    }

    /// <summary>新配置和 AI 生成权限默认关闭，管理权限不会隐含授予它们。</summary>
    [Fact]
    public void ConfigurationAndGenerationRequireIndependentExplicitGrants()
    {
        Assert.DoesNotContain(PermissionIds.CONFIGURE_RECORDINGS, PermissionCatalog.Defaults);
        Assert.DoesNotContain(PermissionIds.GENERATE_ANALYTICS, PermissionCatalog.Defaults);
        var user = new UserProfile("user", "user@example.test", "用户", "", "User", "Active")
        {
            Permissions = new([PermissionIds.MANAGE_RECORDINGS])
        };
        var principal = UserPrincipalFactory.Create(user);
        Assert.False(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, PermissionIds.CONFIGURE_RECORDINGS));
        Assert.False(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, PermissionIds.GENERATE_ANALYTICS));
        Assert.Contains(PermissionIds.CONFIGURE_RECORDINGS, PermissionCatalog.All);
        Assert.Contains(PermissionIds.GENERATE_ANALYTICS, PermissionCatalog.All);
    }
}
