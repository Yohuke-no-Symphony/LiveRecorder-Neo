using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>在隔离数据库验证邮件恢复、原子消费、兼容旧用户和安全失败。</summary>
public sealed class PasswordResetServiceTests
{
    private const string NEW_PASSWORD = "recovered-user-password";

    [MongoFact]
    public async Task CapabilitiesRequireTrustedPublicAddressAndSmtpWithoutDependingOnRegistration()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var service = context.CreateService();
        var settings = await context.Fixture.Settings.GetAsync();
        Assert.False(settings.RegistrationEnabled);
        Assert.True((await service.GetOptionsAsync()).Enabled);
        settings.PublicWebUrl = "";
        await context.Fixture.Settings.SaveAsync(settings);
        Assert.False((await service.GetOptionsAsync()).Enabled);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.RequestAsync(new("unknown@example.test")));
        Assert.Equal(503, error.StatusCode);
        Assert.Empty(context.Email.Messages);
        settings.PublicWebUrl = "https://recorder.example.test";
        settings.Smtp.FromAddress = "invalid";
        await context.Fixture.Settings.SaveAsync(settings);
        Assert.False((await service.GetOptionsAsync()).Enabled);
    }

    [MongoFact]
    public async Task RecoveryUsesTrustedFragmentLinkAndPreservesEveryAccountStatus()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var service = context.CreateService();
        foreach (var status in new[] { "Active", "Pending", "Rejected", "Disabled" })
        {
            var user = await context.CreateUserAsync(status);
            await service.RequestAsync(new(user.Email.ToUpperInvariant()));
            var token = context.LastToken;
            Assert.Equal(43, token.Length);
            Assert.Contains("https://recorder.example.test/reset-password#token=" + token, context.Email.Messages.Last().Body);
            var storedToken = await context.Tokens.Find(item => item.UserId == user.Id).SingleAsync();
            Assert.DoesNotContain(token, storedToken.ToBsonDocument().ToJson());
            Assert.InRange(storedToken.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
            await service.CompleteAsync(new(token, NEW_PASSWORD));
            var updated = await context.Users.Find(item => item.Id == user.Id).SingleAsync();
            Assert.Equal(status, updated.Status);
            Assert.True(updated.EmailVerified);
            Assert.Equal(1, updated.SessionVersion);
            Assert.NotEqual(PasswordVerificationResult.Failed, context.Hasher.VerifyHashedPassword(updated, updated.PasswordHash, NEW_PASSWORD));
            Assert.Equal(PasswordVerificationResult.Failed, context.Hasher.VerifyHashedPassword(updated, updated.PasswordHash, IdentityFixture.USER_PASSWORD));
            Assert.Contains((user.Id, 1L), context.Revoker.Calls);
            Assert.Equal(MessageCode.AUTH_PASSWORD_RESET_TOKEN_INVALID.ToString(),
                (await Assert.ThrowsAsync<InfrastructureException>(() => service.CompleteAsync(new(token, NEW_PASSWORD)))).Code);
        }
    }

    [MongoFact]
    public async Task OAuthOnlyLegacyUserCanSetLocalPasswordAndKeepExternalBinding()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var user = await context.CreateUserAsync(hasPassword: false);
        var bindings = context.Fixture.Context.Collection<ExternalLogin>("external_logins");
        await bindings.InsertOneAsync(new() { ProviderId = "test-provider", Subject = "external-subject", UserId = user.Id });
        await context.Users.UpdateOneAsync(item => item.Id == user.Id, Builders<UserAccount>.Update.Unset(item => item.SessionVersion));
        var service = context.CreateService();
        await service.RequestAsync(new(user.Email));
        var settings = await context.Fixture.Settings.GetAsync();
        settings.PublicWebUrl = "";
        settings.Smtp.Host = "";
        await context.Fixture.Settings.SaveAsync(settings);
        await service.CompleteAsync(new(context.LastToken, NEW_PASSWORD));
        Assert.Equal(user.Id, (await context.Fixture.Identity.AuthenticateAsync(new(user.Email, NEW_PASSWORD))).Id);
        var updated = await context.Users.Find(item => item.Id == user.Id).SingleAsync();
        Assert.Equal(1, updated.SessionVersion);
        Assert.True(updated.EmailVerified);
        Assert.Single(await bindings.Find(item => item.UserId == user.Id).ToListAsync());
    }

    [MongoFact]
    public async Task ConcurrentRequestsShareAtomicEmailCooldownAndUnknownEmailsRemainPrivate()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var user = await context.CreateUserAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => context.CreateService().RequestAsync(new(user.Email))));
        Assert.Single(context.Email.Messages);
        Assert.Equal(1, await context.Tokens.CountDocumentsAsync(FilterDefinition<PasswordResetToken>.Empty));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => context.CreateService().RequestAsync(new("unknown@example.test"))));
        Assert.Single(context.Email.Messages);
        Assert.Equal(2, await context.Throttles.CountDocumentsAsync(FilterDefinition<PasswordResetThrottle>.Empty));
        Assert.Equal(1, context.Email.Attempts);
    }

    [MongoFact]
    public async Task ResentLinksRemainUsableUntilExactlyOneConcurrentResetSucceeds()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var user = await context.CreateUserAsync();
        var service = context.CreateService();
        await service.RequestAsync(new(user.Email));
        var first = context.LastToken;
        await context.AllowResendAsync();
        await service.RequestAsync(new(user.Email));
        var second = context.LastToken;
        Assert.NotEqual(first, second);
        Assert.Equal(2, await context.Tokens.CountDocumentsAsync(FilterDefinition<PasswordResetToken>.Empty));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                await context.CreateService().CompleteAsync(new(index % 2 == 0 ? first : second, NEW_PASSWORD));
                return true;
            }
            catch (InfrastructureException error) when (error.Code == MessageCode.AUTH_PASSWORD_RESET_TOKEN_INVALID.ToString())
            {
                return false;
            }
        }));
        Assert.Single(outcomes, result => result);
        Assert.Single(context.Revoker.Calls);
        Assert.Equal(1, (await context.Users.Find(item => item.Id == user.Id).SingleAsync()).SessionVersion);
        await Assert.ThrowsAsync<InfrastructureException>(() => service.CompleteAsync(new(first, NEW_PASSWORD)));
        await Assert.ThrowsAsync<InfrastructureException>(() => service.CompleteAsync(new(second, NEW_PASSWORD)));
    }

    [MongoFact]
    public async Task ExpirySessionRevocationAndEmailChangesRejectOutstandingLinks()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var service = context.CreateService();
        foreach (var scenario in new[] { "expired", "revoked", "email-changed" })
        {
            var user = await context.CreateUserAsync();
            await service.RequestAsync(new(user.Email));
            var token = context.LastToken;
            if (scenario == "expired")
            {
                await context.Tokens.UpdateOneAsync(item => item.UserId == user.Id,
                    Builders<PasswordResetToken>.Update.Set(item => item.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            }
            else if (scenario == "revoked")
            {
                await context.Users.UpdateOneAsync(item => item.Id == user.Id, Builders<UserAccount>.Update.Inc(item => item.SessionVersion, 1));
            }
            else
            {
                await context.Users.UpdateOneAsync(item => item.Id == user.Id,
                    Builders<UserAccount>.Update.Set(item => item.NormalizedEmail, "changed-" + user.NormalizedEmail));
            }
            Assert.Equal(MessageCode.AUTH_PASSWORD_RESET_TOKEN_INVALID.ToString(),
                (await Assert.ThrowsAsync<InfrastructureException>(() => service.CompleteAsync(new(token, NEW_PASSWORD)))).Code);
            Assert.Equal(user.PasswordHash, (await context.Users.Find(item => item.Id == user.Id).SingleAsync()).PasswordHash);
        }
        Assert.Empty(context.Revoker.Calls);
    }

    [MongoFact]
    public async Task InvalidPasswordsAndMalformedTokensDoNotConsumeValidRecovery()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var user = await context.CreateUserAsync();
        var service = context.CreateService();
        await service.RequestAsync(new(user.Email));
        var token = context.LastToken;
        foreach (var password in new[] { "short", new string('x', 257), new string(' ', 12) })
        {
            Assert.Equal(MessageCode.AUTH_PASSWORD_LENGTH_INVALID.ToString(),
                (await Assert.ThrowsAsync<InfrastructureException>(() => service.CompleteAsync(new(token, password)))).Code);
        }
        foreach (var invalid in new[] { "", new string('x', 42), new string('!', 43), new string('x', 43) })
        {
            Assert.Equal(MessageCode.AUTH_PASSWORD_RESET_TOKEN_INVALID.ToString(),
                (await Assert.ThrowsAsync<InfrastructureException>(() => service.CompleteAsync(new(invalid, NEW_PASSWORD)))).Code);
        }
        await service.CompleteAsync(new(token, NEW_PASSWORD));
        Assert.Single(context.Revoker.Calls);
    }

    [MongoFact]
    public async Task MailFailureRemovesItsTokenAndKeepsGenericResponseAndCooldown()
    {
        await using var context = await PasswordResetTestContext.CreateAsync();
        var user = await context.CreateUserAsync();
        context.Email.Fail = true;
        var service = context.CreateService();
        await service.RequestAsync(new(user.Email));
        Assert.Equal(0, await context.Tokens.CountDocumentsAsync(FilterDefinition<PasswordResetToken>.Empty));
        Assert.Empty(context.Email.Messages);
        Assert.Equal(1, context.Email.Attempts);
        await service.RequestAsync(new(user.Email));
        await service.RequestAsync(new("unknown@example.test"));
        Assert.Equal(1, context.Email.Attempts);
        Assert.Equal(2, await context.Throttles.CountDocumentsAsync(FilterDefinition<PasswordResetThrottle>.Empty));
    }
}
