using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>身份和 B站适配的依赖注册。</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>注册持久化身份、邮件、单次授权流程和采集凭据维护。</summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<OAuthAvatarCache>();
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<IPasswordResetService, PasswordResetService>();
        services.AddSingleton<IInstallationService, InstallationService>();
        services.AddSingleton<BilibiliService>();
        services.AddSingleton<IBilibiliEventConnection, BilibiliEventConnection>();
        services.AddSingleton<IBilibiliService>(provider => provider.GetRequiredService<BilibiliService>());
        services.AddHttpClient("identity", client => client.Timeout = TimeSpan.FromSeconds(20)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient(OAuthAvatarCache.HTTP_CLIENT).ConfigurePrimaryHttpMessageHandler(OAuthAvatarHttp.CreateHandler).RemoveAllLoggers();
        services.AddHttpClient("bilibili", client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient("bilibili-login", client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip
            });
        services.AddHostedService<BilibiliAccountMaintenanceService>();
        return services;
    }
}
