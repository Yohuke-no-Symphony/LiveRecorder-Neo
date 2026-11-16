using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Server.Endpoints;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class CacheCleanupHttpContext : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recorder-cache-cleanup-http-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpBrowser> browsers = [];
    internal AppPaths Paths { get; }
    internal WebApplication App { get; private set; } = null!;
    internal Uri Address { get; private set; } = null!;

    private CacheCleanupHttpContext()
    {
        Paths = new(directory, Path.Combine(directory, "records"));
    }

    internal static async Task<CacheCleanupHttpContext> CreateAsync()
    {
        var context = new CacheCleanupHttpContext();
        try
        {
            context.Paths.EnsureDirectories();
            var builder = WebApplication.CreateBuilder(
                ["--urls", "http://127.0.0.1:0", "--environment", "Production", "--Logging:LogLevel:Default", "Critical"]);
            builder.Services.AddAuthentication(CacheCleanupTestAuthenticationHandler.SCHEME)
                .AddScheme<AuthenticationSchemeOptions, CacheCleanupTestAuthenticationHandler>(CacheCleanupTestAuthenticationHandler.SCHEME, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-CSRF-TOKEN";
                options.Cookie.Name = "lr.csrf";
                options.Cookie.SameSite = SameSiteMode.Strict;
            });
            builder.Services.AddSingleton<IAppPaths>(context.Paths);
            builder.Services.AddSingleton<ISettingsStore>(new BandwidthTestSettingsStore());
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<PlaybackCache>();
            builder.Services.AddSingleton<ArtworkCache>();
            builder.Services.AddSingleton<EventArchiveReader>();
            builder.Services.AddSingleton<AnalyticsContributionService>();
            builder.Services.AddSingleton<StorageUsageService>();
            builder.Services.AddSingleton<CacheCleanupService>();
            builder.Services.AddHttpClient("artwork");

            context.App = builder.Build();
            context.App.Use(async (http, next) =>
            {
                try
                {
                    await next();
                }
                catch (InfrastructureException error) when (!http.Response.HasStarted)
                {
                    await LocalizedProblem.Create(error).ExecuteAsync(http);
                }
                catch (AntiforgeryValidationException) when (!http.Response.HasStarted)
                {
                    await LocalizedProblem.Create(MessageCode.REQUEST_CSRF_EXPIRED, StatusCodes.Status400BadRequest).ExecuteAsync(http);
                }
            });
            context.App.UseRouting();
            context.App.UseAuthentication();
            context.App.UseAuthorization();
            context.App.Use(async (http, next) =>
            {
                if (http.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(http.Request.Method)
                    && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
                {
                    await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
                }
                await next();
            });
            context.App.MapGet("/api/auth/me", (HttpContext http, IAntiforgery antiforgery) =>
                Results.Ok(new { user = (object?)null, csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken }));
            context.App.MapStorageCacheEndpoints();

            await context.App.StartAsync();
            var addresses = context.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            context.Address = new(addresses.Single());
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    internal HttpBrowser NewBrowser(string? role = null)
    {
        var browser = new HttpBrowser(Address);
        if (role is not null)
        {
            browser.Client.DefaultRequestHeaders.Add(CacheCleanupTestAuthenticationHandler.ROLE_HEADER, role);
        }
        browsers.Add(browser);
        return browser;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var browser in browsers)
        {
            browser.Dispose();
        }
        if (App is not null)
        {
            await App.DisposeAsync();
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
