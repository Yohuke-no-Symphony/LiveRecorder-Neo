using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class InstallationNoticeStartupFlow : IApplicationStartupFlow
{
    void IApplicationStartupFlow.Execute(WebApplication app)
    {
        var bootstrap = app.Services.GetRequiredService<IBootstrapStore>();
        if (!bootstrap.IsInstalled)
        {
            app.Logger.LogWarning("First-time installation token: {InstallationToken}", bootstrap.InstallationToken);
        }
    }
}
