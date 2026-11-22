using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Server.Configuration;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal static class InstanceStorageStartupFlow
{
    internal static (string WebRoot, CurrentInstanceStorage CurrentStorage) Prepare(WebApplicationBuilder builder)
    {
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
        return (webRoot, currentStorage);
    }
}
