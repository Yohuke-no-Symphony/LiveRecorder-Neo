using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Server.Endpoints;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class HttpPolicyStartupFlow : IBuilderStartupFlow
{
    void IBuilderStartupFlow.Execute(WebApplicationBuilder builder)
    {
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
            options.AddPolicy("password-reset", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(ClipShareEndpoints.UNLOCK_RATE_LIMIT_POLICY, http =>
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId
                    ? RateLimitPartition.GetFixedWindowLimiter(userId,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("anonymous"));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => http.Request.Path.StartsWithSegments("/api/auth") && !HttpMethods.IsGet(http.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("other"));
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, _) => await LocalizedProblem.Create(MessageCode.REQUEST_RATE_LIMIT, 429).ExecuteAsync(context.HttpContext);
        });
    }
}
