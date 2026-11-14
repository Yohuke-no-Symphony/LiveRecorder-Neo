using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class PasswordResetTestContext(IdentityFixture fixture) : IAsyncDisposable
{
    internal IdentityFixture Fixture { get; } = fixture;
    internal PasswordResetTestEmailSender Email { get; } = new();
    internal CapturingSessionRevoker Revoker { get; } = new();
    internal IPasswordHasher<UserAccount> Hasher { get; } = new PasswordHasher<UserAccount>();
    internal IMongoCollection<UserAccount> Users => Fixture.Context.Collection<UserAccount>("users");
    internal IMongoCollection<PasswordResetToken> Tokens => Fixture.Context.Collection<PasswordResetToken>("password_reset_tokens");
    internal IMongoCollection<PasswordResetThrottle> Throttles => Fixture.Context.Collection<PasswordResetThrottle>("password_reset_throttles");

    internal static async Task<PasswordResetTestContext> CreateAsync()
    {
        var fixture = await IdentityFixture.CreateAsync();
        try
        {
            var settings = await fixture.Settings.GetAsync();
            settings.PublicWebUrl = "https://recorder.example.test";
            settings.Smtp.Host = "smtp.example.test";
            settings.Smtp.FromAddress = "recorder@example.test";
            await fixture.Settings.SaveAsync(settings);
            return new(fixture);
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal PasswordResetService CreateService() => new(Fixture.Context, Fixture.Settings, Email, Hasher, Revoker,
        NullLogger<PasswordResetService>.Instance);

    internal async Task<UserAccount> CreateUserAsync(string status = "Active", bool hasPassword = true)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var user = new UserAccount { Email = email, NormalizedEmail = email, DisplayName = "恢复用户", Status = status };
        if (hasPassword)
        {
            user.PasswordHash = Hasher.HashPassword(user, IdentityFixture.USER_PASSWORD);
        }
        await Users.InsertOneAsync(user);
        return user;
    }

    internal string LastToken => Regex.Match(Email.Messages.Last().Body, "#token=([A-Za-z0-9_-]{43})").Groups[1].Value;

    internal Task AllowResendAsync() => Throttles.UpdateManyAsync(FilterDefinition<PasswordResetThrottle>.Empty,
        Builders<PasswordResetThrottle>.Update.Set(item => item.NextAllowedAtUtc, DateTime.UtcNow.AddSeconds(-1)));

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Fixture.DisposeAsync();
}
