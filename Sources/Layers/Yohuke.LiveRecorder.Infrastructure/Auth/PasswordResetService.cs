using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>以邮箱链接和用户版本条件更新完成一次性密码恢复。</summary>
public sealed class PasswordResetService(IMongoContext context, ISettingsStore settingsStore, IEmailSender emailSender,
    IPasswordHasher<UserAccount> passwordHasher, IUserSessionRevoker sessionRevoker, ILogger<PasswordResetService> logger) : IPasswordResetService
{
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private bool indexesCreated;
    private IMongoCollection<UserAccount> Users => context.Collection<UserAccount>("users");
    private IMongoCollection<PasswordResetToken> Tokens => context.Collection<PasswordResetToken>("password_reset_tokens");
    private IMongoCollection<PasswordResetThrottle> Throttles => context.Collection<PasswordResetThrottle>("password_reset_throttles");

    /// <inheritdoc />
    public async Task<PasswordResetOptions> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        return new(IsConfigured(settings));
    }

    /// <inheritdoc />
    public async Task RequestAsync(PasswordResetRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (!IsConfigured(settings))
        {
            throw new InfrastructureException(MessageCode.AUTH_PASSWORD_RESET_UNAVAILABLE, 503);
        }
        var email = IdentityValidation.NormalizeEmail(request.Email);
        await EnsureIndexesAsync(cancellationToken);
        if (!await AcquireRequestAsync(email, cancellationToken))
        {
            return;
        }
        var user = await Users.Find(item => item.NormalizedEmail == email).FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return;
        }
        var token = IdentityToken.Create();
        var record = new PasswordResetToken
        {
            Id = TokenHash(token),
            UserId = user.Id,
            NormalizedEmail = email,
            IssuedSessionVersion = user.SessionVersion,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        };
        await Tokens.InsertOneAsync(record, cancellationToken: cancellationToken);
        PublicWebUrlPolicy.TryGetBaseUri(settings.PublicWebUrl, out var baseUri);
        var link = new Uri(baseUri!, "reset-password#token=" + token).AbsoluteUri;
        try
        {
            await emailSender.SendAsync(email, LocalizedText.Get(MessageCode.EMAIL_PASSWORD_RESET_SUBJECT),
                LocalizedText.Get(MessageCode.EMAIL_PASSWORD_RESET_BODY, link), cancellationToken);
        }
        catch (Exception error)
        {
            logger.LogWarning("Password reset email delivery failed: {ErrorType}", error.GetType().Name);
            await RemoveTokenAsync(record.Id);
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(PasswordResetCompleteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Token is not { Length: 43 } || request.Token.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')))
        {
            throw InvalidToken();
        }
        IdentityValidation.ValidatePassword(request.NewPassword);
        var tokenHash = TokenHash(request.Token);
        var token = await Tokens.Find(item => item.Id == tokenHash && item.ExpiresAtUtc > DateTime.UtcNow).FirstOrDefaultAsync(cancellationToken);
        if (token is null)
        {
            throw InvalidToken();
        }
        var user = await Users.Find(item => item.Id == token.UserId && item.NormalizedEmail == token.NormalizedEmail).FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.SessionVersion != token.IssuedSessionVersion)
        {
            throw InvalidToken();
        }
        var hash = passwordHasher.HashPassword(user, request.NewPassword);
        if (token.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw InvalidToken();
        }
        var filter = Builders<UserAccount>.Filter;
        var updated = await Users.FindOneAndUpdateAsync(
            filter.Eq(item => item.Id, user.Id) & filter.Eq(item => item.NormalizedEmail, token.NormalizedEmail) & AccountSessionVersion.Filter(token.IssuedSessionVersion),
            Builders<UserAccount>.Update.Set(item => item.PasswordHash, hash).Set(item => item.EmailVerified, true)
                .Set(item => item.UpdatedAtUtc, DateTime.UtcNow).Inc(item => item.SessionVersion, 1),
            new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (updated is null)
        {
            throw InvalidToken();
        }
        sessionRevoker.Revoke(updated.Id, updated.SessionVersion);
        await RemoveTokenAsync(tokenHash);
    }

    private async Task<bool> AcquireRequestAsync(string email, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var id = IdentityToken.Hash("password-reset-request:" + email);
        try
        {
            await Throttles.FindOneAndUpdateAsync(item => item.Id == id && item.NextAllowedAtUtc <= now,
                Builders<PasswordResetThrottle>.Update.Set(item => item.NextAllowedAtUtc, now.AddSeconds(60))
                    .Set(item => item.ExpiresAtUtc, now.AddDays(1)).SetOnInsert(item => item.CreatedAtUtc, now),
                new FindOneAndUpdateOptions<PasswordResetThrottle> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, cancellationToken);
            return true;
        }
        catch (MongoCommandException error) when (error.Code == 11000)
        {
            return false;
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private async Task RemoveTokenAsync(string id)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Tokens.DeleteOneAsync(item => item.Id == id, cleanup.Token);
        }
        catch (Exception error) when (error is MongoException or OperationCanceledException)
        {
            logger.LogWarning("Password reset token cleanup failed: {ErrorType}", error.GetType().Name);
        }
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (indexesCreated)
        {
            return;
        }
        await indexGate.WaitAsync(cancellationToken);
        try
        {
            if (indexesCreated)
            {
                return;
            }
            await Tokens.Indexes.CreateOneAsync(new CreateIndexModel<PasswordResetToken>(
                Builders<PasswordResetToken>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);
            await Throttles.Indexes.CreateOneAsync(new CreateIndexModel<PasswordResetThrottle>(
                Builders<PasswordResetThrottle>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);
            indexesCreated = true;
        }
        finally
        {
            indexGate.Release();
        }
    }

    private static bool IsConfigured(ServerSettings settings) =>
        PublicWebUrlPolicy.TryGetBaseUri(settings.PublicWebUrl, out _) && SmtpEmailSender.IsConfigured(settings.Smtp);

    private static string TokenHash(string token) => IdentityToken.Hash("password-reset:" + token);
    private static InfrastructureException InvalidToken() => new(MessageCode.AUTH_PASSWORD_RESET_TOKEN_INVALID);
}
