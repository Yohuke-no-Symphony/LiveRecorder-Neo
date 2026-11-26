using Yohuke.LiveRecorder.Application.Localization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using EntityTagHeaderValue = Microsoft.Net.Http.Headers.EntityTagHeaderValue;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>映射 Cookie 身份、审核、邀请与服务器端 OAuth 流程。</summary>
public static class IdentityEndpointExtensions
{
    private const string OAUTH_BINDING_COOKIE = "lr.oauth.binding";
    private const string OAUTH_REGISTRATION_COOKIE = "lr.oauth.registration";

    /// <summary>注册身份端点；调用方负责统一 CSRF 与异常中间件。</summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints, IReadOnlyCollection<string>? allowedWebUiOrigins = null)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapAccountSettingsEndpoints();
        auth.MapGet("/permissions", () => Results.Ok(PermissionCatalog.Definitions)).RequireAuthorization();
        auth.MapGet(UserAvatarFiles.ROUTE_PREFIX + "{id}", async (string id, string? v, HttpContext http, UserAvatarService avatars, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-cache";
            var avatar = await avatars.ReadAsync(id, v, ct);
            return avatar is null
                ? Results.NotFound()
                : Results.Bytes(avatar.Content, avatar.ContentType, entityTag: new EntityTagHeaderValue(avatar.ETag));
        }).RequireAuthorization();
        auth.MapGet("/me", async (HttpContext http, IIdentityService identity, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = id is null ? null : await identity.GetUserAsync(id, ct);
            if (user is { Status: not "Active" })
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                http.User = new(new ClaimsIdentity());
                user = null;
            }
            return Results.Ok(new { user, csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken });
        });
        auth.MapPut("/language", async (LanguagePreferenceRequest request, HttpContext http, IIdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.SetLanguageAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, request, ct))).RequireAuthorization();
        auth.MapPut("/analytics-preferences", async (AnalyticsPreferencesRequest request, HttpContext http, IIdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.SetAnalyticsPreferencesAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, request, ct))).RequireAuthorization();
        auth.MapPut("/playback-preferences", async (PlaybackPreferencesRequest request, HttpContext http, IIdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.SetPlaybackPreferencesAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, request, ct))).RequireAuthorization();
        auth.MapPost("/login", async (LoginRequest request, HttpContext http, IIdentityService identity, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            var user = await identity.AuthenticateAsync(request, ct);
            await SignInAsync(http, user);
            return Results.Ok(new { user, csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken });
        });
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            http.User = new(new ClaimsIdentity());
            return Results.NoContent();
        });
        auth.MapPost("/email-code", async (EmailCodeRequest request, IIdentityService identity, CancellationToken ct) =>
        {
            await identity.SendEmailCodeAsync(request, ct);
            return Results.NoContent();
        });
        auth.MapGet("/password-reset/options", async (HttpContext http, IPasswordResetService reset, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reset.GetOptionsAsync(ct));
        });
        auth.MapPost("/password-reset/request", async (PasswordResetRequest request, IPasswordResetService reset, CancellationToken ct) =>
        {
            await reset.RequestAsync(request, ct);
            return Results.NoContent();
        }).RequireRateLimiting("password-reset");
        auth.MapPost("/password-reset/complete", async (PasswordResetCompleteRequest request, HttpContext http, IPasswordResetService reset, CancellationToken ct) =>
        {
            await reset.CompleteAsync(request, ct);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            http.User = new(new ClaimsIdentity());
            return Results.NoContent();
        });
        auth.MapGet("/registration-options", async (HttpContext http, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var settings = await settingsStore.GetAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new RegistrationOptions(settings.RegistrationEnabled, settings.RegistrationApprovalRequired, settings.RegistrationInvitationRequired, settings.RegistrationEmailVerificationRequired));
        });
        auth.MapPost("/register", async (RegisterRequest request, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.RegisterAsync(request, ct)));
        auth.MapGet("/identities", async (HttpContext http, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.ListExternalProvidersAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct))).RequireAuthorization();
        auth.MapGet("/oauth/providers", async (ISettingsStore settings, CancellationToken ct) => Results.Ok((await settings.GetAsync(ct)).OAuthProviders.Where(x => x.Enabled).Select(x => new { x.Id, x.Name })));
        auth.MapPost("/oauth/{providerId}/begin", async (string providerId, HttpContext http, IIdentityService identity, CancellationToken ct) =>
        {
            var binding = GetBrowserBinding(http);
            var callback = GetOAuthCallback(http.Request, providerId, allowedWebUiOrigins);
            return Results.Ok(await identity.BeginOAuthAsync(providerId, callback, binding, ct));
        });
        auth.MapPost("/oauth/{providerId}/bind", async (string providerId, HttpContext http, IIdentityService identity, CancellationToken ct) =>
        {
            var binding = GetBrowserBinding(http);
            var callback = GetOAuthCallback(http.Request, providerId, allowedWebUiOrigins);
            return Results.Ok(await identity.BeginOAuthBindingAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, providerId, callback, binding, ct));
        }).RequireAuthorization();
        auth.MapPost("/oauth/{providerId}/email-change", async (string providerId, OAuthEmailChangeRequest request, HttpContext http,
            IIdentityService identity, IAccountSettingsService settings, CancellationToken ct) =>
        {
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var sessionVersion = UserPrincipalFactory.GetSessionVersion(http.User)!.Value;
            var email = await settings.ValidateOAuthEmailChangeAsync(userId, sessionVersion, request.Email, ct);
            var binding = GetBrowserBinding(http);
            var callback = GetOAuthCallback(http.Request, providerId, allowedWebUiOrigins);
            return Results.Ok(await identity.BeginOAuthEmailChangeAsync(userId, sessionVersion, email, providerId, callback, binding, ct));
        }).RequireAuthorization();
        auth.MapPost("/oauth/{providerId}/exchange", async (string providerId, OAuthExchangeRequest request, HttpContext http,
            IIdentityService identity, IAccountSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            var result = await identity.CompleteOAuthAsync(providerId, request.State, request.Code, http.Request.Cookies[OAUTH_BINDING_COOKIE] ?? "",
                http.User.FindFirstValue(ClaimTypes.NameIdentifier), UserPrincipalFactory.GetSessionVersion(http.User), ct);
            if (result.EmailChangeAuthorization is { } authorization)
            {
                await settings.RequestOAuthEmailChangeAsync(authorization, ct);
            }
            else if (result.User is { } user)
            {
                await SignInAsync(http, user);
                http.Response.Cookies.Delete(OAUTH_REGISTRATION_COOKIE, new CookieOptions { Path = "/api/auth/oauth" });
            }
            else if (result.RegistrationTicket is { } ticket)
            {
                http.Response.Cookies.Append(OAUTH_REGISTRATION_COOKIE, ticket, CookieOptions(http, "/api/auth/oauth"));
            }
            return Results.Ok(new
            {
                result.User,
                registrationRequired = result.RegistrationTicket is not null,
                emailChangeStarted = result.EmailChangeAuthorization is not null,
                result.Email,
                result.DisplayName,
                csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken
            });
        });
        auth.MapPost("/oauth/register", async (OAuthRegistrationRequest request, HttpContext http, IIdentityService identity, CancellationToken ct) =>
        {
            var ticket = http.Request.Cookies[OAUTH_REGISTRATION_COOKIE] ?? throw new InfrastructureException(MessageCode.AUTH_OAUTH_REGISTRATION_EXPIRED);
            var user = await identity.CompleteOAuthRegistrationAsync(ticket, request, ct);
            http.Response.Cookies.Delete(OAUTH_REGISTRATION_COOKIE, new CookieOptions { Path = "/api/auth/oauth" });
            return Results.Ok(user);
        });
        var admin = endpoints.MapGroup("/api/admin").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapGet("/users", async (IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.ListUsersAsync(ct)));
        admin.MapPost("/users/{id}/approval", async (string id, ApprovalRequest request, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.SetApprovalAsync(id, request.Approved, ct)));
        admin.MapPost("/users/{id}/ban", async (string id, HttpContext http, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.SetBannedAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, id, true, ct)));
        admin.MapPost("/users/{id}/unban", async (string id, HttpContext http, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.SetBannedAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, id, false, ct)));
        admin.MapPost("/users/{id}/role", async (string id, RoleRequest request, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.SetRoleAsync(id, request.Role, ct)));
        admin.MapPut("/users/{id}/permissions", async (string id, PermissionSet request, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.SetPermissionsAsync(id, request, ct)));
        admin.MapGet("/invitations", async (IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.ListInvitationsAsync(ct)));
        admin.MapPost("/invitations", async (InvitationRequest request, HttpContext http, IIdentityService identity, CancellationToken ct) => Results.Ok(await identity.CreateInvitationAsync(request, http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct)));
        admin.MapDelete("/invitations/{id}", async (string id, IIdentityService identity, CancellationToken ct) =>
        {
            await identity.RevokeInvitationAsync(id, ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static string GetOAuthCallback(HttpRequest request, string providerId, IReadOnlyCollection<string>? allowedWebUiOrigins)
    {
        var origin = request.Headers.Origin.ToString();
        var callbackOrigin = allowedWebUiOrigins?.Contains(origin, StringComparer.Ordinal) == true
            ? origin
            : $"{request.Scheme}://{request.Host}";
        return $"{callbackOrigin}{request.PathBase}/oauth/callback/{Uri.EscapeDataString(providerId)}";
    }

    private static async Task SignInAsync(HttpContext http, UserProfile user)
    {
        var principal = UserPrincipalFactory.Create(user);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
        http.User = principal;
    }

    private static CookieOptions CookieOptions(HttpContext http, string path) => new() { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = path, MaxAge = TimeSpan.FromMinutes(10), IsEssential = true };

    private static string GetBrowserBinding(HttpContext http)
    {
        var binding = http.Request.Cookies[OAUTH_BINDING_COOKIE];
        if (string.IsNullOrWhiteSpace(binding))
        {
            binding = IdentityToken.Create();
        }
        http.Response.Cookies.Append(OAUTH_BINDING_COOKIE, binding, CookieOptions(http, "/api/auth/oauth"));
        return binding;
    }
}
