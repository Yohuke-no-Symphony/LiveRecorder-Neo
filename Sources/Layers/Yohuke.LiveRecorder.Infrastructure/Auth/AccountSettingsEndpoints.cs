using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Media;

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
        account.MapPut("/avatar", async (HttpContext http, UserAvatarService avatars, IAvatarImageEncoder encoder, CancellationToken ct) =>
        {
            var request = http.Request;
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
                !contentType.MediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                throw new InfrastructureException(MessageCode.AUTH_AVATAR_INVALID, 415);
            }
            var limit = IAvatarImageEncoder.MAX_IMAGE_BYTES;
            if (request.ContentLength > limit)
            {
                throw new InfrastructureException(MessageCode.AUTH_AVATAR_TOO_LARGE, 413);
            }
            var input = new byte[limit + 1];
            var length = await request.Body.ReadAtLeastAsync(input, input.Length, false, ct);
            if (length > limit)
            {
                throw new InfrastructureException(MessageCode.AUTH_AVATAR_TOO_LARGE, 413);
            }
            var content = await encoder.EncodeAsync(input.AsMemory(0, length), ct);
            return Results.Ok(await avatars.SetAsync(UserId(http), SessionVersion(http), content, ct));
        });
        account.MapDelete("/avatar", async (HttpContext http, UserAvatarService avatars, CancellationToken ct) =>
            Results.Ok(await avatars.ClearAsync(UserId(http), SessionVersion(http), ct)));
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
