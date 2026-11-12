using System.Security.Claims;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
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

namespace Yohuke.LiveRecorder.Server;

/// <summary>服务组合入口，可由原生宿主与集成测试创建。</summary>
public static class ServerStartup
{
    /// <summary>构建安装、鉴权、后台录制与媒体交付管线。</summary>
    public static WebApplication CreateApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
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
        var paths = new AppPaths(builder.Configuration["DataDirectory"]);
        var webRoot = Path.Combine(AppContext.BaseDirectory, "webui");
        if (!Directory.Exists(webRoot) && builder.Environment.IsDevelopment())
        {
            webRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "WebUI", "dist"));
        }
        var webUi = new WebUiLocation(webRoot);
        webUi.EnsurePrivate(paths.DataDirectory);
        builder.Services.AddSingleton(webUi);
        builder.Services.AddSingleton(paths);
        var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys"))).SetApplicationName("Yohuke.LiveRecorder");
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi();
        }
        builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        builder.Services.AddSingleton<BootstrapStore>();
        builder.Services.AddSingleton<MongoContext>();
        builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<EventArchiveReader>();
        builder.Services.AddSingleton<LibraryService>();
        builder.Services.AddSingleton<SessionGroupingService>();
        builder.Services.AddSingleton<SessionEventReader>();
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddSingleton<PlaybackService>();
        builder.Services.AddSingleton<MediaJobService>();
        builder.Services.AddSingleton<AnalyticsService>();
        builder.Services.AddSingleton<ArtworkCache>();
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
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "lr.csrf";
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
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
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
        });
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            try
            {
                http.Response.Headers.XContentTypeOptions = "nosniff";
                await next();
            }
            catch (InfrastructureException error) when (!http.Response.HasStarted)
            {
                await Results.Problem(statusCode: error.StatusCode, detail: error.Message).ExecuteAsync(http);
            }
            catch (AntiforgeryValidationException) when (!http.Response.HasStarted)
            {
                await Results.Problem(statusCode: 400, detail: "请求校验已失效，请刷新后重试。").ExecuteAsync(http);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
            }
            catch (MongoException error) when (!http.Response.HasStarted)
            {
                app.Logger.LogWarning("数据库请求失败：{Message}", error.Message);
                await Results.Problem(statusCode: 503, detail: "数据库暂时不可用。").ExecuteAsync(http);
            }
            catch (TimeoutException error) when (!http.Response.HasStarted)
            {
                app.Logger.LogWarning("请求等待超时：{Message}", error.Message);
                await Results.Problem(statusCode: 503, detail: "服务暂时无响应，请稍后重试。").ExecuteAsync(http);
            }
            catch (Exception error) when (!http.Response.HasStarted)
            {
                app.Logger.LogError(error, "请求失败");
                await Results.Problem(statusCode: 500, detail: "操作失败，请查看服务端日志。").ExecuteAsync(http);
            }
        });
        app.UseForwardedHeaders();
        app.UseRouting();
        if (developmentOrigins.Length > 0)
        {
            app.UseCors();
        }
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
        app.MapIdentityEndpoints(developmentOrigins);
        app.MapBilibiliEndpoints();
        app.MapLibraryEndpoints();
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
                await Results.Problem(statusCode: 404, detail: "接口不存在。").ExecuteAsync(http);
                return;
            }
            var index = Path.Combine(webRoot, "index.html");
            if (!File.Exists(index))
            {
                await Results.Problem(statusCode: 503, detail: "WebUI 尚未构建。").ExecuteAsync(http);
                return;
            }
            http.Response.ContentType = "text/html; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            await http.Response.SendFileAsync(index);
        });
        var bootstrap = app.Services.GetRequiredService<BootstrapStore>();
        if (!bootstrap.IsInstalled)
        {
            app.Logger.LogWarning("首次安装令牌：{InstallationToken}", bootstrap.InstallationToken);
        }
        return app;
    }
}
