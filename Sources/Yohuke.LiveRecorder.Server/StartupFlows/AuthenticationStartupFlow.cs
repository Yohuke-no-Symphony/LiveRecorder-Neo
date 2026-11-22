using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Server.Configuration;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class AuthenticationStartupFlow(CurrentInstanceStorage currentStorage) : IBuilderStartupFlow
{
    void IBuilderStartupFlow.Execute(WebApplicationBuilder builder)
    {
        var antiforgeryCookieName = currentStorage.IsInstalled ? "lr.csrf" : "lr.setup.csrf." + Guid.NewGuid().ToString("N");
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = antiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "lr.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                return LocalizedProblem.Create(MessageCode.REQUEST_UNAUTHORIZED, StatusCodes.Status401Unauthorized).ExecuteAsync(context.HttpContext);
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                return LocalizedProblem.Create(MessageCode.REQUEST_FORBIDDEN, StatusCodes.Status403Forbidden).ExecuteAsync(context.HttpContext);
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                if (!currentStorage.IsInstalled)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }
                var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var version = UserPrincipalFactory.GetSessionVersion(context.Principal);
                var identity = context.HttpContext.RequestServices.GetRequiredService<IIdentityService>();
                var user = id is null || version is null ? null : await identity.ValidateSessionAsync(id, version.Value, context.HttpContext.RequestAborted);
                if (user is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
                else
                {
                    var principal = UserPrincipalFactory.Create(user);
                    var currentClaims = context.Principal!.Claims.Select(claim => (claim.Type, claim.Value));
                    var updatedClaims = principal.Claims.Select(claim => (claim.Type, claim.Value));
                    if (!currentClaims.SequenceEqual(updatedClaims))
                    {
                        context.ReplacePrincipal(principal);
                        context.ShouldRenew = true;
                    }
                }
            };
        });
        builder.Services.AddSingleton<IAuthenticationSchemeProvider, InstanceAuthenticationSchemeProvider>();
        builder.Services.AddAuthorization(options =>
        {
            foreach (var permission in PermissionCatalog.Definitions.Select(definition => definition.Id))
            {
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    UserPermissionNames.IsGranted(context.User, permission)));
            }
        });
    }
}
