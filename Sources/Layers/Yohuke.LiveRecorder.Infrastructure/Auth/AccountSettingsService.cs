using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>以账号文档的条件更新提交自助设置和一次性邮箱验证。</summary>
public sealed class AccountSettingsService(IMongoContext context, ISettingsStore settingsStore, IEmailSender emailSender,
    IPasswordHasher<UserAccount> passwordHasher, IUserSessionRevoker sessionRevoker, TimeProvider timeProvider,
    ILogger<AccountSettingsService> logger) : IAccountSettingsService
{
    private const int MAX_CODE_ATTEMPTS = 5;
    private static readonly TimeSpan CODE_WINDOW = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SEND_COOLDOWN = TimeSpan.FromSeconds(60);
    private IMongoCollection<UserAccount> Users => context.Collection<UserAccount>("users");
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<AccountSettingsResponse> GetSettingsAsync(string userId, long sessionVersion, CancellationToken cancellationToken = default)
    {
        var user = await RequireCurrentUserAsync(userId, sessionVersion, cancellationToken);
        var canChangeEmail = SmtpEmailSender.IsConfigured((await settingsStore.GetAsync(cancellationToken)).Smtp);
        var pending = CurrentChallenge(user, UtcNow);
        return new(HasLocalPassword(user), canChangeEmail, pending is null ? null : Response(pending));
    }

    /// <inheritdoc />
    public async Task<UserProfile> ChangeDisplayNameAsync(string userId, long sessionVersion, DisplayNameChangeRequest request, CancellationToken cancellationToken = default)
    {
        var name = IdentityValidation.NormalizeDisplayName(request.DisplayName);
        var filter = ActiveSession(userId, sessionVersion);
        var update = Builders<UserAccount>.Update.Set(user => user.DisplayName, name).Set(user => user.UpdatedAtUtc, UtcNow);
        var user = await Users.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.REQUEST_UNAUTHORIZED, 401);
        return IdentityService.Profile(user);
    }

    /// <inheritdoc />
    public async Task ChangePasswordAsync(string userId, long sessionVersion, PasswordChangeRequest request, CancellationToken cancellationToken = default)
    {
        IdentityValidation.ValidatePassword(request.NewPassword);
        var user = await RequireCurrentUserAsync(userId, sessionVersion, cancellationToken);
        if (!HasLocalPassword(user))
        {
            throw new InfrastructureException(MessageCode.AUTH_LOCAL_PASSWORD_REQUIRED, 403);
        }
        VerifyPassword(user, request.CurrentPassword);
        var hash = passwordHasher.HashPassword(user, request.NewPassword);
        var filter = ActiveSession(userId, sessionVersion);
        var update = Builders<UserAccount>.Update.Set(value => value.PasswordHash, hash).Set(value => value.UpdatedAtUtc, UtcNow)
            .Unset(value => value.PendingEmailChange).Inc(value => value.SessionVersion, 1);
        var updated = await Users.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.REQUEST_UNAUTHORIZED, 401);
        sessionRevoker.Revoke(updated.Id, updated.SessionVersion);
    }

    /// <inheritdoc />
    public async Task<PendingEmailChangeResponse> RequestEmailChangeAsync(string userId, long sessionVersion, EmailChangeRequest request, CancellationToken cancellationToken = default)
    {
        var user = await RequireCurrentUserAsync(userId, sessionVersion, cancellationToken);
        var email = await ValidateEmailTargetAsync(user, request.Email, cancellationToken);
        var now = UtcNow;
        var pending = CurrentChallenge(user, now);
        if (pending?.Email != email)
        {
            if (!HasLocalPassword(user))
            {
                throw new InfrastructureException(MessageCode.AUTH_EMAIL_CHANGE_REAUTH_REQUIRED, 403);
            }
            VerifyPassword(user, request.CurrentPassword);
        }
        var expiresAt = pending?.Email == email ? pending.ExpiresAtUtc : now.Add(CODE_WINDOW);
        return await SendCodeAsync(user, email, now, expiresAt, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ValidateOAuthEmailChangeAsync(string userId, long sessionVersion, string email, CancellationToken cancellationToken = default)
    {
        var user = await RequireCurrentUserAsync(userId, sessionVersion, cancellationToken);
        RequireNoLocalPassword(user);
        return await ValidateEmailTargetAsync(user, email, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RequestOAuthEmailChangeAsync(OAuthEmailChangeAuthorization authorization, CancellationToken cancellationToken = default)
    {
        var user = await RequireCurrentUserAsync(authorization.UserId, authorization.SessionVersion, cancellationToken);
        RequireNoLocalPassword(user);
        var email = await ValidateEmailTargetAsync(user, authorization.Email, cancellationToken);
        var now = UtcNow;
        await SendCodeAsync(user, email, now, now.Add(CODE_WINDOW), cancellationToken);
    }

    /// <inheritdoc />
    public async Task ConfirmEmailChangeAsync(string userId, long sessionVersion, EmailChangeConfirmRequest request, CancellationToken cancellationToken = default)
    {
        var email = IdentityValidation.NormalizeEmail(request.Email);
        if (request.Code is not { Length: 6 } || !request.Code.All(char.IsAsciiDigit))
        {
            throw InvalidCode();
        }
        var filter = Builders<UserAccount>.Filter;
        var active = ActiveSession(userId, sessionVersion)
                     & filter.Eq(user => user.PendingEmailChange!.Email, email)
                     & filter.Eq(user => user.PendingEmailChange!.IssuedSessionVersion, sessionVersion);
        var attempt = active
                      & filter.Gt(user => user.PendingEmailChange!.ExpiresAtUtc, UtcNow)
                      & filter.Ne(user => user.PendingEmailChange!.CodeHash, null)
                      & filter.Lt(user => user.PendingEmailChange!.Attempts, MAX_CODE_ATTEMPTS);
        var previous = await Users.FindOneAndUpdateAsync(attempt,
            Builders<UserAccount>.Update.Inc(user => user.PendingEmailChange!.Attempts, 1), cancellationToken: cancellationToken);
        if (previous?.PendingEmailChange is not { CodeHash: { } hash, Salt: { } salt } challenge ||
            !IdentityToken.EqualsHash(hash, IdentityToken.CodeHash(email, request.Code, salt)))
        {
            throw InvalidCode();
        }
        await IdentityService.EnsureUniqueEmailIndexAsync(Users, cancellationToken);
        var now = UtcNow;
        var commit = active
                     & filter.Eq(user => user.PendingEmailChange!.ChallengeId, challenge.ChallengeId)
                     & filter.Eq(user => user.PendingEmailChange!.CodeHash, hash)
                     & filter.Gt(user => user.PendingEmailChange!.ExpiresAtUtc, now);
        var update = Builders<UserAccount>.Update.Set(user => user.Email, email).Set(user => user.NormalizedEmail, email)
            .Set(user => user.EmailVerified, true).Set(user => user.UpdatedAtUtc, now).Unset(user => user.PendingEmailChange)
            .Inc(user => user.SessionVersion, 1);
        try
        {
            var updated = await Users.FindOneAndUpdateAsync(commit, update,
                new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken) ?? throw InvalidCode();
            sessionRevoker.Revoke(updated.Id, updated.SessionVersion);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_ALREADY_REGISTERED, 409);
        }
        catch (MongoCommandException error) when (error.Code == 11000)
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_ALREADY_REGISTERED, 409);
        }
    }

    private async Task<string> ValidateEmailTargetAsync(UserAccount user, string email, CancellationToken cancellationToken)
    {
        var normalized = IdentityValidation.NormalizeEmail(email);
        if (normalized == user.NormalizedEmail)
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CHANGE_SAME_EMAIL);
        }
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (!SmtpEmailSender.IsConfigured(settings.Smtp))
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CHANGE_UNAVAILABLE, 503);
        }
        if (await Users.Find(value => value.NormalizedEmail == normalized).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_ALREADY_REGISTERED, 409);
        }
        return normalized;
    }

    private async Task<PendingEmailChangeResponse> SendCodeAsync(UserAccount user, string email, DateTime now,
        DateTime expiresAt, CancellationToken cancellationToken)
    {
        var current = CurrentChallenge(user, now);
        if (current?.NextSendAtUtc > now)
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CODE_RATE_LIMIT, 429);
        }
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var salt = IdentityToken.Create();
        var pending = new PendingEmailChange
        {
            ChallengeId = IdentityToken.Create(),
            Email = email,
            IssuedSessionVersion = user.SessionVersion,
            CodeHash = IdentityToken.CodeHash(email, code, salt),
            Salt = salt,
            ExpiresAtUtc = expiresAt,
            NextSendAtUtc = now.Add(SEND_COOLDOWN)
        };
        var filter = Builders<UserAccount>.Filter;
        var snapshot = user.PendingEmailChange is { } previous
            ? filter.Eq(value => value.PendingEmailChange!.ChallengeId, previous.ChallengeId)
            : filter.Eq(value => value.PendingEmailChange, null);
        var result = await Users.UpdateOneAsync(ActiveSession(user.Id, user.SessionVersion) & snapshot,
            Builders<UserAccount>.Update.Set(value => value.PendingEmailChange, pending), cancellationToken: cancellationToken);
        if (result.MatchedCount == 0)
        {
            await RequireCurrentUserAsync(user.Id, user.SessionVersion, cancellationToken);
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CODE_RATE_LIMIT, 429);
        }
        try
        {
            await emailSender.SendAsync(email, LocalizedText.Get(MessageCode.EMAIL_CHANGE_SUBJECT),
                LocalizedText.Get(MessageCode.EMAIL_CHANGE_BODY, code), cancellationToken);
        }
        catch (Exception error)
        {
            await InvalidateCodeAsync(user.Id, pending.ChallengeId);
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            logger.LogWarning("Email change verification delivery failed: {ErrorType}", error.GetType().Name);
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CHANGE_DELIVERY_FAILED, 503);
        }
        return Response(pending);
    }

    private async Task InvalidateCodeAsync(string userId, string challengeId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Users.UpdateOneAsync(user => user.Id == userId && user.PendingEmailChange!.ChallengeId == challengeId,
                Builders<UserAccount>.Update.Unset(user => user.PendingEmailChange!.CodeHash).Unset(user => user.PendingEmailChange!.Salt),
                cancellationToken: cleanup.Token);
        }
        catch (Exception error) when (error is MongoException or OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Email change verification cleanup failed: {ErrorType}", error.GetType().Name);
        }
    }

    private async Task<UserAccount> RequireCurrentUserAsync(string userId, long sessionVersion, CancellationToken cancellationToken) =>
        await Users.Find(ActiveSession(userId, sessionVersion)).FirstOrDefaultAsync(cancellationToken)
        ?? throw new InfrastructureException(MessageCode.REQUEST_UNAUTHORIZED, 401);

    private static FilterDefinition<UserAccount> ActiveSession(string userId, long sessionVersion) =>
        Builders<UserAccount>.Filter.Where(user => user.Id == userId && user.Status == "Active") & AccountSessionVersion.Filter(sessionVersion);

    private static bool HasLocalPassword(UserAccount user) => !string.IsNullOrEmpty(user.PasswordHash);

    private static void RequireNoLocalPassword(UserAccount user)
    {
        if (HasLocalPassword(user))
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CHANGE_PASSWORD_REQUIRED, 403);
        }
    }

    private void VerifyPassword(UserAccount user, string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256 ||
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            throw new InfrastructureException(MessageCode.AUTH_INVALID_CREDENTIALS, 401);
        }
    }

    private static PendingEmailChange? CurrentChallenge(UserAccount user, DateTime now) =>
        user.PendingEmailChange is { } pending && pending.IssuedSessionVersion == user.SessionVersion && pending.ExpiresAtUtc > now ? pending : null;

    private static PendingEmailChangeResponse Response(PendingEmailChange pending) =>
        new(pending.Email, pending.ExpiresAtUtc, pending.NextSendAtUtc);

    private static InfrastructureException InvalidCode() => new(MessageCode.AUTH_EMAIL_CODE_INVALID);
}
