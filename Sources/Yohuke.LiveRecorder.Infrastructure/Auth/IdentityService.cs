using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Domain.Localization;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>以 MongoDB 保存账号与单次身份流程，统一执行普通注册与外部注册要求。</summary>
public sealed class IdentityService(IMongoContext context, ISettingsStore settingsStore, IEmailSender emailSender, ISecretProtector protector, IPasswordHasher<UserAccount> passwordHasher, IHttpClientFactory httpClientFactory, IUserSessionRevoker sessionRevoker) : IIdentityService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private bool indexesCreated;
    private IMongoCollection<UserAccount> Users => context.Collection<UserAccount>("users");
    private IMongoCollection<EmailVerificationCode> Codes => context.Collection<EmailVerificationCode>("email_codes");
    private IMongoCollection<Invitation> Invitations => context.Collection<Invitation>("invitations");

    /// <inheritdoc />
    public async Task<UserProfile> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var email = IdentityValidation.NormalizeEmail(request.Email);
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
        {
            throw new InfrastructureException(MessageCode.AUTH_INVALID_CREDENTIALS, 401);
        }
        var user = await Users.Find(x => x.NormalizedEmail == email).FirstOrDefaultAsync(cancellationToken);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash) || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            throw new InfrastructureException(MessageCode.AUTH_INVALID_CREDENTIALS, 401);
        }
        EnsureActive(user);
        return Profile(user);
    }

    /// <inheritdoc />
    public async Task<UserProfile?> GetUserAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await Users.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
        return user is null ? null : Profile(user);
    }

    /// <inheritdoc />
    public async Task<UserProfile> SetLanguageAsync(string id, LanguagePreferenceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Language is not null && !SupportedLanguages.IsSupported(request.Language))
        {
            throw new InfrastructureException(MessageCode.LANGUAGE_UNSUPPORTED);
        }
        var user = await Users.FindOneAndUpdateAsync(x => x.Id == id && x.Status == "Active",
            Builders<UserAccount>.Update.Set(x => x.Language, request.Language).Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
            new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.AUTH_USER_NOT_FOUND, 404);
        return Profile(user);
    }

    /// <inheritdoc />
    public async Task<UserProfile> SetAnalyticsPreferencesAsync(string id, AnalyticsPreferencesRequest request, CancellationToken cancellationToken = default)
    {
        var user = await Users.FindOneAndUpdateAsync(x => x.Id == id && x.Status == "Active",
            Builders<UserAccount>.Update.Set(x => x.AnalyticsRangePinned, request.RangePinned).Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
            new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.AUTH_USER_NOT_FOUND, 404);
        return Profile(user);
    }

    /// <inheritdoc />
    public async Task<UserProfile?> ValidateSessionAsync(string id, long sessionVersion = 0, CancellationToken cancellationToken = default)
    {
        var profile = await GetUserAsync(id, cancellationToken);
        return profile is { Status: "Active" } && profile.SessionVersion == sessionVersion ? profile : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserProfile>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        (await Users.Find(FilterDefinition<UserAccount>.Empty).SortByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken)).Select(Profile).ToArray();

    /// <inheritdoc />
    public async Task SendEmailCodeAsync(EmailCodeRequest request, CancellationToken cancellationToken = default)
    {
        var email = IdentityValidation.NormalizeEmail(request.Email);
        if (request.Purpose != "register")
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_PURPOSE_UNSUPPORTED);
        }
        var settings = await EnsureRegistrationEnabledAsync(cancellationToken);
        if (!settings.RegistrationEmailVerificationRequired)
        {
            return;
        }
        await EnsureIndexesAsync(cancellationToken);
        EmailVerificationCode record;
        string code;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var id = IdentityToken.Hash($"register:{email}");
            var previous = await Codes.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (previous is not null && previous.CreatedAtUtc > DateTime.UtcNow.AddSeconds(-60))
            {
                throw new InfrastructureException(MessageCode.AUTH_EMAIL_CODE_RATE_LIMIT, 429);
            }
            code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
            var salt = IdentityToken.Create();
            record = new() { Id = id, Email = email, Salt = salt, CodeHash = IdentityToken.CodeHash(email, code, salt), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10) };
            await Codes.ReplaceOneAsync(x => x.Id == id, record, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
        try
        {
            await emailSender.SendAsync(email, LocalizedText.Get(MessageCode.EMAIL_VERIFICATION_SUBJECT), LocalizedText.Get(MessageCode.EMAIL_VERIFICATION_BODY, code), cancellationToken);
        }
        catch
        {
            await Codes.DeleteOneAsync(x => x.Id == record.Id && x.CodeHash == record.CodeHash, CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<UserProfile> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var email = IdentityValidation.NormalizeEmail(request.Email);
        var name = IdentityValidation.NormalizeDisplayName(request.DisplayName);
        IdentityValidation.ValidatePassword(request.Password);
        var user = new UserAccount { Email = email, NormalizedEmail = email, DisplayName = name };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        return await RegisterCoreAsync(user, request.EmailCode, request.InvitationCode, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserProfile> CompleteOAuthRegistrationAsync(string ticket, OAuthRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_REGISTRATION_EXPIRED);
        }
        var registrations = context.Collection<OAuthRegistration>("oauth_registrations");
        var id = IdentityToken.Hash(ticket);
        var registration = await registrations.Find(x => x.Id == id && x.ExpiresAtUtc > DateTime.UtcNow).FirstOrDefaultAsync(cancellationToken)
                           ?? throw new InfrastructureException(MessageCode.AUTH_OAUTH_REGISTRATION_EXPIRED);
        var email = IdentityValidation.NormalizeEmail(request.Email);
        var user = new UserAccount { Email = email, NormalizedEmail = email, DisplayName = IdentityValidation.NormalizeDisplayName(request.DisplayName), AvatarUrl = registration.AvatarUrl };
        var external = new ExternalLogin { Id = ExternalId(registration.ProviderId, registration.Subject), ProviderId = registration.ProviderId, Subject = registration.Subject, UserId = user.Id };
        var result = await RegisterCoreAsync(user, request.EmailCode, request.InvitationCode, external, cancellationToken);
        await registrations.DeleteOneAsync(x => x.Id == id, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task<UserProfile> SetApprovalAsync(string id, bool approved, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var user = await RequireUserAsync(id, cancellationToken);
            if (user.Status == "Disabled")
            {
                throw new InfrastructureException(MessageCode.AUTH_USER_STATUS_CONFLICT, 409);
            }
            if (!approved)
            {
                await EnsureNotLastAdministratorAsync(user, cancellationToken);
            }
            var revoke = !approved && user.Status == "Active";
            var update = Builders<UserAccount>.Update.Set(x => x.Status, approved ? "Active" : "Rejected").Set(x => x.UpdatedAtUtc, DateTime.UtcNow);
            if (revoke)
            {
                update = update.Inc(x => x.SessionVersion, 1);
            }
            user = await Users.FindOneAndUpdateAsync(x => x.Id == id && x.Status == user.Status, update,
                new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
                ?? throw new InfrastructureException(MessageCode.AUTH_USER_STATUS_CONFLICT, 409);
            if (revoke)
            {
                sessionRevoker.Revoke(id, user.SessionVersion);
            }
            return Profile(user);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserProfile> SetBannedAsync(string actorId, string id, bool banned, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var actor = await RequireUserAsync(actorId, cancellationToken);
            if (actor is not { Role: "Admin", Status: "Active" })
            {
                throw new InfrastructureException(MessageCode.REQUEST_FORBIDDEN, 403);
            }
            if (banned && actorId == id)
            {
                throw new InfrastructureException(MessageCode.AUTH_SELF_BAN_FORBIDDEN, 409);
            }
            var user = await RequireUserAsync(id, cancellationToken);
            var targetStatus = banned ? "Disabled" : "Active";
            if (user.Status == targetStatus)
            {
                return Profile(user);
            }
            if (user.Status != (banned ? "Active" : "Disabled"))
            {
                throw new InfrastructureException(MessageCode.AUTH_USER_STATUS_CONFLICT, 409);
            }
            if (banned)
            {
                await EnsureNotLastAdministratorAsync(user, cancellationToken);
            }
            var update = Builders<UserAccount>.Update.Set(x => x.Status, targetStatus).Set(x => x.UpdatedAtUtc, DateTime.UtcNow);
            if (banned)
            {
                update = update.Inc(x => x.SessionVersion, 1);
            }
            user = await Users.FindOneAndUpdateAsync(x => x.Id == id && x.Status == user.Status, update,
                new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
                ?? throw new InfrastructureException(MessageCode.AUTH_USER_STATUS_CONFLICT, 409);
            if (banned)
            {
                sessionRevoker.Revoke(id, user.SessionVersion);
            }
            return Profile(user);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserProfile> SetRoleAsync(string id, string role, CancellationToken cancellationToken = default)
    {
        if (role is not ("Admin" or "User"))
        {
            throw new InfrastructureException(MessageCode.AUTH_INVALID_ROLE);
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var user = await RequireUserAsync(id, cancellationToken);
            if (role != "Admin")
            {
                await EnsureNotLastAdministratorAsync(user, cancellationToken);
            }
            user = await Users.FindOneAndUpdateAsync(x => x.Id == id,
                Builders<UserAccount>.Update.Set(x => x.Role, role).Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
                new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
                ?? throw new InfrastructureException(MessageCode.AUTH_USER_NOT_FOUND, 404);
            return Profile(user);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserProfile> SetPermissionsAsync(string id, PermissionSet permissions, CancellationToken cancellationToken = default)
    {
        if (!PermissionCatalog.IsValid(permissions))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var user = await Users.FindOneAndUpdateAsync(x => x.Id == id,
                Builders<UserAccount>.Update.Set(x => x.Permissions, permissions).Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
                new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, cancellationToken)
                ?? throw new InfrastructureException(MessageCode.AUTH_USER_NOT_FOUND, 404);
            return Profile(user);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<InvitationSummary> CreateInvitationAsync(InvitationRequest request, string createdBy, CancellationToken cancellationToken = default)
    {
        if (request.MaxUses is < 1 or > 10000 || request.ExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow)
        {
            throw new InfrastructureException(MessageCode.AUTH_INVALID_INVITATION_LIMIT);
        }
        await EnsureIndexesAsync(cancellationToken);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var invitation = new Invitation { CodeHash = IdentityToken.Hash(code), CreatedBy = createdBy, MaxUses = request.MaxUses, ExpiresAtUtc = request.ExpiresAtUtc?.ToUniversalTime() };
        await Invitations.InsertOneAsync(invitation, cancellationToken: cancellationToken);
        return InvitationProfile(invitation, code);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvitationSummary>> ListInvitationsAsync(CancellationToken cancellationToken = default) =>
        (await Invitations.Find(FilterDefinition<Invitation>.Empty).SortByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken)).Select(x => InvitationProfile(x)).ToArray();

    /// <inheritdoc />
    public async Task RevokeInvitationAsync(string id, CancellationToken cancellationToken = default) =>
        _ = await Invitations.UpdateOneAsync(x => x.Id == id, Builders<Invitation>.Update.Set(x => x.Revoked, true), cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<OAuthAuthorizationResult> BeginOAuthAsync(string providerId, string redirectUri, string browserBinding, CancellationToken cancellationToken = default) =>
        BeginOAuthCoreAsync(providerId, redirectUri, browserBinding, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListExternalProvidersAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureActive(await RequireUserAsync(userId, cancellationToken));
        return (await context.Collection<ExternalLogin>("external_logins").Find(x => x.UserId == userId).ToListAsync(cancellationToken)).Select(x => x.ProviderId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public async Task<OAuthAuthorizationResult> BeginOAuthBindingAsync(string userId, string providerId, string redirectUri, string browserBinding, CancellationToken cancellationToken = default)
    {
        EnsureActive(await RequireUserAsync(userId, cancellationToken));
        return await BeginOAuthCoreAsync(providerId, redirectUri, browserBinding, userId, cancellationToken);
    }

    private async Task<OAuthAuthorizationResult> BeginOAuthCoreAsync(string providerId, string redirectUri, string browserBinding, string? linkUserId, CancellationToken cancellationToken)
    {
        var provider = await GetProviderAsync(providerId, cancellationToken);
        ValidateEndpoint(redirectUri);
        if (string.IsNullOrEmpty(browserBinding))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_BROWSER_BINDING_MISSING);
        }
        await EnsureIndexesAsync(cancellationToken);
        var state = IdentityToken.Create();
        var verifier = IdentityToken.Create();
        var flow = new OAuthFlow { Id = IdentityToken.Hash(state), ProviderId = providerId, Intent = linkUserId is null ? "Login" : "Bind", LinkUserId = linkUserId, RedirectUri = redirectUri, BrowserBindingHash = IdentityToken.Hash(browserBinding), ProtectedVerifier = protector.Protect(verifier), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10) };
        await context.Collection<OAuthFlow>("oauth_flows").InsertOneAsync(flow, cancellationToken: cancellationToken);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var parameters = new Dictionary<string, string> { ["response_type"] = "code", ["client_id"] = provider.ClientId, ["redirect_uri"] = redirectUri, ["scope"] = provider.Scope, ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256" };
        var separator = provider.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        return new(provider.AuthorizationEndpoint + separator + string.Join('&', parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}")), flow.ExpiresAtUtc);
    }

    /// <inheritdoc />
    public Task<OAuthLoginResult> CompleteOAuthAsync(string providerId, string state, string code, string browserBinding, CancellationToken cancellationToken = default) =>
        CompleteOAuthAsync(providerId, state, code, browserBinding, null, cancellationToken);

    /// <inheritdoc />
    public async Task<OAuthLoginResult> CompleteOAuthAsync(string providerId, string state, string code, string browserBinding, string? currentUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code) || code.Length > 4096 || string.IsNullOrWhiteSpace(browserBinding))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_CALLBACK_INVALID);
        }
        var hash = IdentityToken.Hash(state);
        var bindingHash = IdentityToken.Hash(browserBinding);
        var flows = context.Collection<OAuthFlow>("oauth_flows");
        var flow = await flows.Find(x => x.Id == hash && x.ProviderId == providerId && x.BrowserBindingHash == bindingHash && x.ExpiresAtUtc > DateTime.UtcNow).FirstOrDefaultAsync(cancellationToken)
                   ?? throw new InfrastructureException(MessageCode.AUTH_OAUTH_FLOW_UNAVAILABLE);
        if (flow.Intent is not ("Login" or "Bind"))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_INTENT_INVALID);
        }
        if (flow.Intent == "Bind")
        {
            if (string.IsNullOrWhiteSpace(currentUserId) || currentUserId != flow.LinkUserId)
            {
                throw new InfrastructureException(MessageCode.AUTH_OAUTH_BINDING_USER_MISMATCH, 403);
            }
            EnsureActive(await RequireUserAsync(currentUserId, cancellationToken));
        }
        if (await flows.FindOneAndDeleteAsync(x => x.Id == hash && x.ProviderId == providerId && x.BrowserBindingHash == bindingHash && x.Intent == flow.Intent && x.LinkUserId == flow.LinkUserId && x.ExpiresAtUtc > DateTime.UtcNow, cancellationToken: cancellationToken) is null)
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_FLOW_EXPIRED);
        }
        var provider = await GetProviderAsync(providerId, cancellationToken);
        using var client = httpClientFactory.CreateClient("identity");
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, provider.TokenEndpoint);
        var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = protector.Unprotect(flow.ProtectedVerifier), ["redirect_uri"] = flow.RedirectUri, ["client_id"] = provider.ClientId };
        var secret = string.IsNullOrEmpty(provider.ClientSecret) ? "" : protector.Unprotect(provider.ClientSecret);
        if (provider.UseBasicAuthentication)
        {
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(provider.ClientId)}:{Uri.EscapeDataString(secret)}")));
        }
        else if (secret.Length > 0)
        {
            form["client_secret"] = secret;
        }
        tokenRequest.Content = new FormUrlEncodedContent(form);
        using var tokenResponse = await client.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_CODE_REJECTED);
        }
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
        var accessToken = tokens?["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_TOKEN_INVALID, 502);
        }
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, provider.UserInfoEndpoint);
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var infoResponse = await client.SendAsync(infoRequest, cancellationToken);
        if (!infoResponse.IsSuccessStatusCode)
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_IDENTITY_UNVERIFIABLE, 502);
        }
        var info = await infoResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken) ?? throw new InfrastructureException(MessageCode.AUTH_OAUTH_PROFILE_EMPTY, 502);
        var subject = ReadField(info, provider.SubjectField);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_SUBJECT_MISSING, 502);
        }
        var externalId = ExternalId(providerId, subject);
        if (flow.Intent == "Bind")
        {
            return await BindIdentityAsync(flow.LinkUserId!, providerId, subject, externalId, cancellationToken);
        }
        var existing = await context.Collection<ExternalLogin>("external_logins").Find(x => x.Id == externalId).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var user = await RequireUserAsync(existing.UserId, cancellationToken);
            EnsureActive(user);
            return new(Profile(user), null, user.Email, user.DisplayName);
        }
        await EnsureRegistrationEnabledAsync(cancellationToken);
        var ticket = IdentityToken.Create();
        var registration = new OAuthRegistration { Id = IdentityToken.Hash(ticket), ProviderId = providerId, Subject = subject, Email = ReadField(info, provider.EmailField), DisplayName = ReadField(info, provider.NameField), AvatarUrl = ReadField(info, provider.AvatarField), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10) };
        await context.Collection<OAuthRegistration>("oauth_registrations").InsertOneAsync(registration, cancellationToken: cancellationToken);
        return new(null, ticket, registration.Email, registration.DisplayName);
    }

    private async Task<OAuthLoginResult> BindIdentityAsync(string userId, string providerId, string subject, string externalId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var user = await RequireUserAsync(userId, cancellationToken);
            EnsureActive(user);
            var logins = context.Collection<ExternalLogin>("external_logins");
            var existing = await logins.Find(x => x.Id == externalId).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null && existing.UserId != userId)
            {
                throw new InfrastructureException(MessageCode.AUTH_OAUTH_BOUND_ELSEWHERE, 409);
            }
            if (existing is null)
            {
                await logins.InsertOneAsync(new ExternalLogin { Id = externalId, ProviderId = providerId, Subject = subject, UserId = userId }, cancellationToken: cancellationToken);
            }
            return new(Profile(user), null, user.Email, user.DisplayName);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_ALREADY_BOUND, 409);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<UserProfile> RegisterCoreAsync(UserAccount user, string? code, string? invitationCode, ExternalLogin? external, CancellationToken cancellationToken)
    {
        var settings = await EnsureRegistrationEnabledAsync(cancellationToken);
        if (settings.RegistrationEmailVerificationRequired && (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsAsciiDigit)))
        {
            throw new InfrastructureException(MessageCode.AUTH_EMAIL_CODE_INVALID);
        }
        if (settings.RegistrationInvitationRequired && (string.IsNullOrWhiteSpace(invitationCode) || invitationCode.Length > 128))
        {
            throw new InfrastructureException(MessageCode.AUTH_INVITATION_INVALID);
        }
        await EnsureIndexesAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        Invitation? reserved = null;
        var insertedUser = false;
        try
        {
            if (await Users.Find(x => x.NormalizedEmail == user.NormalizedEmail).AnyAsync(cancellationToken))
            {
                throw new InfrastructureException(MessageCode.AUTH_EMAIL_ALREADY_REGISTERED, 409);
            }
            if (external is not null && await context.Collection<ExternalLogin>("external_logins").Find(x => x.Id == external.Id).AnyAsync(cancellationToken))
            {
                throw new InfrastructureException(MessageCode.AUTH_OAUTH_ALREADY_REGISTERED, 409);
            }
            EmailVerificationCode? verification = null;
            if (settings.RegistrationEmailVerificationRequired)
            {
                var codeId = IdentityToken.Hash($"register:{user.NormalizedEmail}");
                verification = await Codes.FindOneAndUpdateAsync(x => x.Id == codeId && !x.Consumed && x.ExpiresAtUtc > DateTime.UtcNow && x.Attempts < 5, Builders<EmailVerificationCode>.Update.Inc(x => x.Attempts, 1), cancellationToken: cancellationToken);
                if (verification is null || !IdentityToken.EqualsHash(verification.CodeHash, IdentityToken.CodeHash(user.NormalizedEmail, code!, verification.Salt)))
                {
                    throw new InfrastructureException(MessageCode.AUTH_EMAIL_CODE_INVALID);
                }
            }
            if (settings.RegistrationInvitationRequired)
            {
                var invitationHash = IdentityToken.Hash(invitationCode!.Trim().ToUpperInvariant());
                var invitation = await Invitations.Find(x => x.CodeHash == invitationHash && !x.Revoked && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow)).FirstOrDefaultAsync(cancellationToken);
                if (invitation is null || invitation.UsedCount >= invitation.MaxUses)
                {
                    throw new InfrastructureException(MessageCode.AUTH_INVITATION_INVALID);
                }
                reserved = await Invitations.FindOneAndUpdateAsync(x => x.Id == invitation.Id && !x.Revoked && x.UsedCount == invitation.UsedCount, Builders<Invitation>.Update.Inc(x => x.UsedCount, 1), cancellationToken: cancellationToken);
                if (reserved is null)
                {
                    throw new InfrastructureException(MessageCode.AUTH_INVITATION_CONFLICT, 409);
                }
            }
            user.Status = settings.RegistrationApprovalRequired ? "Pending" : "Active";
            user.EmailVerified = verification is not null;
            user.Permissions = settings.DefaultUserPermissions;
            await Users.InsertOneAsync(user, cancellationToken: cancellationToken);
            insertedUser = true;
            if (external is not null)
            {
                await context.Collection<ExternalLogin>("external_logins").InsertOneAsync(external, cancellationToken: cancellationToken);
            }
            if (verification is not null)
            {
                await Codes.UpdateOneAsync(x => x.Id == verification.Id && x.CodeHash == verification.CodeHash, Builders<EmailVerificationCode>.Update.Set(x => x.Consumed, true), cancellationToken: cancellationToken);
            }
            return Profile(user);
        }
        catch
        {
            if (insertedUser)
            {
                await Users.DeleteOneAsync(x => x.Id == user.Id, CancellationToken.None);
                if (external is not null)
                {
                    await context.Collection<ExternalLogin>("external_logins").DeleteOneAsync(x => x.Id == external.Id && x.UserId == user.Id, CancellationToken.None);
                }
            }
            if (reserved is not null)
            {
                await Invitations.UpdateOneAsync(x => x.Id == reserved.Id && x.UsedCount > 0, Builders<Invitation>.Update.Inc(x => x.UsedCount, -1), cancellationToken: CancellationToken.None);
            }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ServerSettings> EnsureRegistrationEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (!settings.RegistrationEnabled)
        {
            throw new InfrastructureException(MessageCode.AUTH_REGISTRATION_CLOSED, 403);
        }
        return settings;
    }

    private async Task<OAuthProviderSettings> GetProviderAsync(string id, CancellationToken cancellationToken)
    {
        var provider = (await settingsStore.GetAsync(cancellationToken)).OAuthProviders.FirstOrDefault(x => x.Id == id && x.Enabled)
                       ?? throw new InfrastructureException(MessageCode.AUTH_OAUTH_PROVIDER_DISABLED, 404);
        ValidateEndpoint(provider.AuthorizationEndpoint);
        ValidateEndpoint(provider.TokenEndpoint);
        ValidateEndpoint(provider.UserInfoEndpoint);
        if (string.IsNullOrWhiteSpace(provider.ClientId))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_CLIENT_ID_MISSING, 503);
        }
        return provider;
    }

    private static void ValidateEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
        {
            throw new InfrastructureException(MessageCode.AUTH_OAUTH_HTTPS_REQUIRED);
        }
    }

    private async Task<UserAccount> RequireUserAsync(string id, CancellationToken cancellationToken) =>
        await Users.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken) ?? throw new InfrastructureException(MessageCode.AUTH_USER_NOT_FOUND, 404);

    private async Task EnsureNotLastAdministratorAsync(UserAccount user, CancellationToken cancellationToken)
    {
        if (user.Role == "Admin" && user.Status == "Active" && await Users.CountDocumentsAsync(x => x.Role == "Admin" && x.Status == "Active", cancellationToken: cancellationToken) <= 1)
        {
            throw new InfrastructureException(MessageCode.AUTH_LAST_ADMIN_REQUIRED, 409);
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
            await Users.Indexes.CreateOneAsync(new CreateIndexModel<UserAccount>(Builders<UserAccount>.IndexKeys.Ascending(x => x.NormalizedEmail), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
            await Invitations.Indexes.CreateOneAsync(new CreateIndexModel<Invitation>(Builders<Invitation>.IndexKeys.Ascending(x => x.CodeHash), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
            await Codes.Indexes.CreateOneAsync(new CreateIndexModel<EmailVerificationCode>(Builders<EmailVerificationCode>.IndexKeys.Ascending(x => x.ExpiresAtUtc), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);
            await context.Collection<OAuthFlow>("oauth_flows").Indexes.CreateOneAsync(new CreateIndexModel<OAuthFlow>(Builders<OAuthFlow>.IndexKeys.Ascending(x => x.ExpiresAtUtc), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);
            await context.Collection<OAuthRegistration>("oauth_registrations").Indexes.CreateOneAsync(new CreateIndexModel<OAuthRegistration>(Builders<OAuthRegistration>.IndexKeys.Ascending(x => x.ExpiresAtUtc), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);
            indexesCreated = true;
        }
        finally
        {
            indexGate.Release();
        }
    }

    private static string ReadField(JsonObject value, string path)
    {
        JsonNode? current = value;
        foreach (var part in path.Split('.'))
        {
            current = current is JsonObject obj ? obj[part] : null;
        }
        return current is JsonValue item ? item.ToString() : "";
    }

    private static string ExternalId(string provider, string subject) => IdentityToken.Hash($"{provider}\n{subject}");
    private static UserProfile Profile(UserAccount user) => new(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.Role, user.Status, user.Language)
    {
        SessionVersion = user.SessionVersion,
        AnalyticsRangePinned = user.AnalyticsRangePinned,
        Permissions = user.Role == "Admin"
            ? PermissionCatalog.All
            : user.Permissions
    };
    private static InvitationSummary InvitationProfile(Invitation invitation, string? code = null) => new(invitation.Id, invitation.CreatedAtUtc, invitation.ExpiresAtUtc, invitation.MaxUses, invitation.UsedCount, invitation.Revoked, code);

    private static void EnsureActive(UserAccount user)
    {
        if (user.Status != "Active")
        {
            throw new InfrastructureException(user.Status == "Pending" ? MessageCode.AUTH_PENDING_APPROVAL : MessageCode.AUTH_ACCOUNT_DISABLED, 403);
        }
    }
}
