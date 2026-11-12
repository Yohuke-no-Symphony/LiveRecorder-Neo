using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>持久化全局设置并缓存最近的有效快照。</summary>
public sealed class SettingsStore(MongoContext context, BootstrapStore bootstrap) : ISettingsStore
{
    private ServerSettings? cached;

    /// <inheritdoc />
    public async Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cached is not null)
        {
            return cached;
        }
        cached = await context.Collection<ServerSettings>("settings").Find(x => x.Id == "server").FirstOrDefaultAsync(cancellationToken)
                 ?? new ServerSettings { StoragePath = bootstrap.Current?.StoragePath ?? "" };
        return cached;
    }

    /// <inheritdoc />
    public async Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Id = "server";
        await context.Collection<ServerSettings>("settings").ReplaceOneAsync(x => x.Id == "server", settings, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        cached = settings;
    }
}
