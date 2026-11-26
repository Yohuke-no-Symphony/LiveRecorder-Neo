using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal static class AccountSettingsEndpoints
{
    internal static void MapAccountSettingsEndpoints(this RouteGroupBuilder auth)
    {
        var account = auth.MapGroup("/me").RequireAuthorization();
        account.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        account.MapGet("/settings", async (HttpContext http, IAccountSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetSettingsAsync(UserId(http), SessionVersion(http), ct)));
        account.MapPut("/display-name", async (DisplayNameChangeRequest request, HttpContext http, IAccountSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.ChangeDisplayNameAsync(UserId(http), SessionVersion(http), request, ct)));
        account.MapPut("/password", async (PasswordChangeRequest request, HttpContext http, IAccountSettingsService settings, CancellationToken ct) =>
        {
            await settings.ChangePasswordAsync(UserId(http), SessionVersion(http), request, ct);
            return await SignOutAsync(http);
        });
        account.MapPost("/email-change/request", async (EmailChangeRequest request, HttpContext http, IAccountSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.RequestEmailChangeAsync(UserId(http), SessionVersion(http), request, ct)));
        account.MapPost("/email-change/confirm", async (EmailChangeConfirmRequest request, HttpContext http, IAccountSettingsService settings, CancellationToken ct) =>
        {
            await settings.ConfirmEmailChangeAsync(UserId(http), SessionVersion(http), request, ct);
            return await SignOutAsync(http);
        });
    }

    private static string UserId(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private static long SessionVersion(HttpContext http) => UserPrincipalFactory.GetSessionVersion(http.User)!.Value;

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.User = new(new ClaimsIdentity());
        return Results.NoContent();
    }
}
