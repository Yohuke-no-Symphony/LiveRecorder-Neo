using System.Security.Claims;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.FileProviders;
using MongoDB.Driver;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain.Localization;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Server.Endpoints;
using Yohuke.LiveRecorder.Server.Realtime;
using Yohuke.LiveRecorder.Server.Configuration;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server;

/// <summary>服务组合入口，可由原生宿主与集成测试创建。</summary>
public static class ServerStartup
{
    /// <summary>构建安装、鉴权、后台录制与媒体交付管线。</summary>
    public static WebApplication CreateApplication(string[] args)
    {
        if (Environment.GetEnvironmentVariable("LIVE_RECORDER_DATA_DIR") is not null
            || args.Any(value => value.Equals("--DataDirectory", StringComparison.OrdinalIgnoreCase) || value.StartsWith("--DataDirectory=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("不再支持 --DataDirectory 或 LIVE_RECORDER_DATA_DIR。请使用 --config 指定启动 JSON，并在其中配置 DataDirectory。");
        }
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Configuration["DataDirectory"] is not null)
        {
            throw new InvalidOperationException("不再支持独立的 DataDirectory 启动配置。请使用 --config 指定启动 JSON，并在其中配置 DataDirectory。");
        }
        if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls("http://127.0.0.1:4784");
        }
        var developmentOrigins = builder.Environment.IsDevelopment()
            ? builder.Configuration.GetSection("WebUI:DevelopmentOrigins").Get<string[]>() ?? []
            : [];
        if (developmentOrigins.Length > 0)
        {
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(developmentOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
        }
        var startupConfiguration = new StartupConfigurationStore(builder.Configuration["config"]);
        var webRoot = Path.Combine(AppContext.BaseDirectory, "webui");
        if (!Directory.Exists(webRoot) && builder.Environment.IsDevelopment())
        {
            webRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "WebUI", "dist"));
        }
        var webUi = new WebUiLocation(webRoot);
        webUi.EnsurePrivate(startupConfiguration.ConfigFile);
        webUi.EnsurePrivate(startupConfiguration.ResolvedDataDirectory);
        webUi.EnsurePrivate(startupConfiguration.ResolvedRecordsDirectory);
        var storageFactory = new InstanceStorageFactory();
        var initialStorage = storageFactory.CreateInitial(startupConfiguration.ResolvedDataDirectory, startupConfiguration.ResolvedRecordsDirectory, startupConfiguration.IsConfigured);
        var currentStorage = new CurrentInstanceStorage(initialStorage);
        var paths = initialStorage.Paths;
        builder.Services.AddSingleton(webUi);
        builder.Services.AddSingleton(startupConfiguration);
        builder.Services.AddSingleton(storageFactory);
        builder.Services.AddSingleton(currentStorage);
        builder.Services.AddSingleton<IAppPaths, CurrentAppPaths>();
        builder.Services.AddDataProtection()
            .SetApplicationName("Yohuke.LiveRecorder")
            .AddKeyManagementOptions(options => options.XmlRepository = new InMemoryKeyRepository());
        builder.Services.AddSingleton<IDataProtectionProvider, CurrentDataProtectionProvider>();
        builder.Services.AddSingleton<ISecretProtector, CurrentSecretProtector>();
        builder.Services.AddSingleton<IBootstrapStore, CurrentBootstrapStore>();
        builder.Services.AddSingleton<IMongoContext, CurrentMongoContext>();
        builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<EventArchiveReader>();
        builder.Services.AddSingleton<LibraryService>();
        builder.Services.AddSingleton<SessionBookmarkService>();
        builder.Services.AddSingleton<SessionGroupingService>();
        builder.Services.AddSingleton<SessionEventReader>();
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddSingleton<PlaybackService>();
        builder.Services.AddSingleton<MediaJobService>();
        builder.Services.AddSingleton<AnalyticsService>();
        builder.Services.AddSingleton<ArtworkCache>();
        builder.Services.AddSingleton<IUserAvatarSource>(services => new BilibiliUserAvatarSource(services.GetRequiredService<IHttpClientFactory>().CreateClient("bilibili")));
        builder.Services.AddSingleton<AudienceAvatarService>();
        builder.Services.AddSingleton<EventArtworkService>();
        builder.Services.AddSingleton<SessionTimelineService>();
        builder.Services.AddSingleton<StatusConnections>();
        builder.Services.AddSingleton<IStatusPublisher, SignalRStatusPublisher>();
        builder.Services.AddHttpClient("artwork", client => client.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddIdentityServices();
        builder.Services.AddLiveRecorderMedia();
        builder.Services.AddHostedService<DatabaseIndexWorker>();
        builder.Services.AddHostedService<RecordingSupervisor>();
        builder.Services.AddHostedService<MediaJobWorker>();
        builder.Services.AddHostedService<StorageMaintenanceWorker>();
        builder.Services.AddHostedService<ArtworkMaintenanceWorker>();
        builder.Services.AddSignalR();
        builder.Services.AddOpenApi();
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
                var identity = context.HttpContext.RequestServices.GetRequiredService<IIdentityService>();
                var user = id is null ? null : await identity.ValidateSessionAsync(id, context.HttpContext.RequestAborted);
                if (user is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
                else if (context.Principal?.FindFirstValue(ClaimTypes.Role) != user.Role)
                {
                    context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.DisplayName),
                        new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Role, user.Role)
                    }, CookieAuthenticationDefaults.AuthenticationScheme)));
                    context.ShouldRenew = true;
                }
            };
        });
        builder.Services.AddSingleton<IAuthenticationSchemeProvider, InstanceAuthenticationSchemeProvider>();
        builder.Services.AddAuthorization();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var address in builder.Configuration.GetSection("KnownProxies").Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(address));
            }
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => http.Request.Path.StartsWithSegments("/api/auth") && !HttpMethods.IsGet(http.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("other"));
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, _) => await LocalizedProblem.Create(MessageCode.REQUEST_RATE_LIMIT, 429).ExecuteAsync(context.HttpContext);
        });
        var app = builder.Build();
        var localization = new RequestLocalizationOptions
        {
            ApplyCurrentCultureToResponseHeaders = true,
            RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()]
        };
        localization.SetDefaultCulture(SupportedLanguages.DEFAULT)
            .AddSupportedCultures(SupportedLanguages.CHINESE, SupportedLanguages.ENGLISH, SupportedLanguages.JAPANESE)
            .AddSupportedUICultures(SupportedLanguages.CHINESE, SupportedLanguages.ENGLISH, SupportedLanguages.JAPANESE);
        app.UseRequestLocalization(localization);
        app.Use(async (http, next) =>
        {
            try
            {
                http.Response.Headers.XContentTypeOptions = "nosniff";
                await next();
            }
            catch (InfrastructureException error) when (!http.Response.HasStarted)
            {
                await LocalizedProblem.Create(error).ExecuteAsync(http);
            }
            catch (AntiforgeryValidationException) when (!http.Response.HasStarted)
            {
                await LocalizedProblem.Create(MessageCode.REQUEST_CSRF_EXPIRED, 400).ExecuteAsync(http);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
            }
            catch (MongoException error) when (!http.Response.HasStarted)
            {
                app.Logger.LogWarning("数据库请求失败：{Message}", error.Message);
                await LocalizedProblem.Create(MessageCode.REQUEST_DATABASE_UNAVAILABLE, 503).ExecuteAsync(http);
            }
            catch (TimeoutException error) when (!http.Response.HasStarted)
            {
                app.Logger.LogWarning("请求等待超时：{Message}", error.Message);
                await LocalizedProblem.Create(MessageCode.REQUEST_TIMEOUT, 503).ExecuteAsync(http);
            }
            catch (Exception error) when (!http.Response.HasStarted)
            {
                app.Logger.LogError(error, "请求失败");
                await LocalizedProblem.Create(MessageCode.REQUEST_FAILED, 500).ExecuteAsync(http);
            }
        });
        app.UseStatusCodePages(async context =>
        {
            if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
            {
                var status = context.HttpContext.Response.StatusCode;
                var code = status switch
                {
                    401 => MessageCode.REQUEST_UNAUTHORIZED,
                    403 => MessageCode.REQUEST_FORBIDDEN,
                    404 => MessageCode.REQUEST_ENDPOINT_NOT_FOUND,
                    429 => MessageCode.REQUEST_RATE_LIMIT,
                    _ => MessageCode.REQUEST_INVALID
                };
                await LocalizedProblem.Create(code, status).ExecuteAsync(context.HttpContext);
            }
        });
        app.UseForwardedHeaders();
        app.UseRouting();
        if (developmentOrigins.Length > 0)
        {
            app.UseCors();
        }
        app.Use(async (http, next) =>
        {
            if (!currentStorage.IsInstalled && http.Request.Path.StartsWithSegments("/api")
                && http.Request.Path != "/api/setup" && http.Request.Path != "/api/setup/status"
                && http.Request.Path != "/api/auth/me" && http.Request.Path != "/api/ui-settings")
            {
                await LocalizedProblem.Create(MessageCode.SETUP_REQUIRED, 503).ExecuteAsync(http);
                return;
            }
            await next();
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            await next();
        });
        app.MapSetupEndpoints();
        app.MapUiSettingsEndpoints();
        app.MapIdentityEndpoints(developmentOrigins);
        app.MapBilibiliEndpoints();
        app.MapLibraryEndpoints();
        app.MapSessionBookmarkEndpoints();
        app.MapEventArtworkEndpoints();
        app.MapSessionTimelineEndpoints();
        app.MapManagementEndpoints();
        app.MapHub<StatusHub>("/hubs/status", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().RequireAuthorization(policy => policy.RequireRole("Admin"));
        }
        if (Directory.Exists(webRoot))
        {
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });
        }
        app.MapFallback(async http =>
        {
            if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/hubs"))
            {
                await LocalizedProblem.Create(MessageCode.REQUEST_ENDPOINT_NOT_FOUND, 404).ExecuteAsync(http);
                return;
            }
            var index = Path.Combine(webRoot, "index.html");
            if (!File.Exists(index))
            {
                await LocalizedProblem.Create(MessageCode.REQUEST_WEBUI_NOT_BUILT, 503).ExecuteAsync(http);
                return;
            }
            http.Response.ContentType = "text/html; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            await http.Response.SendFileAsync(index);
        });
        var bootstrap = app.Services.GetRequiredService<IBootstrapStore>();
        if (!bootstrap.IsInstalled)
        {
            app.Logger.LogWarning("首次安装令牌：{InstallationToken}", bootstrap.InstallationToken);
        }
        return app;
    }
}
