using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

/// <summary>发现并注册基础设施程序集中的实例迁移。</summary>
public static class MigrationServiceCollectionExtensions
{
    private static readonly Version Legacy = new(0, 1);
    private static readonly Version Target = new(0, 2);

    /// <summary>验证版本链并注册每段迁移的独立依赖作用域。</summary>
    public static IServiceCollection AddMigrations(this IServiceCollection services) =>
        services.AddMigrations(Legacy, Target, typeof(MigrationManager).Assembly.GetTypes());

    internal static IServiceCollection AddMigrations(this IServiceCollection services, Version legacyVersion,
        Version targetVersion, IEnumerable<Type> candidates)
    {
        var catalog = new MigrationCatalog(legacyVersion, targetVersion, candidates);
        foreach (var implementation in catalog.Implementations)
        {
            services.AddScoped(implementation);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new MigrationManager(catalog, provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<MigrationManager>>()));
        return services;
    }
}
