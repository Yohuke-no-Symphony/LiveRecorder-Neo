using System.Reflection;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

internal sealed class MigrationCatalog
{
    private readonly Dictionary<string, MigrationStep> steps = new(StringComparer.Ordinal);

    internal MigrationCatalog(Version legacyVersion, Version targetVersion, IEnumerable<Type> candidates)
    {
        if (legacyVersion.Build >= 0 || targetVersion.Build >= 0 || targetVersion < legacyVersion)
        {
            throw new InvalidOperationException("Migration versions must use major.minor in ascending order.");
        }
        Legacy = legacyVersion.ToString();
        Target = targetVersion.ToString();
        foreach (var candidate in candidates)
        {
            var interval = candidate.GetCustomAttribute<MigrationAttribute>();
            var isMigration = !candidate.IsAbstract && !candidate.ContainsGenericParameters && typeof(IMigration).IsAssignableFrom(candidate);
            if (interval is null && !isMigration)
            {
                continue;
            }
            if (interval is null || !isMigration || !TryParse(interval.From, out var from) ||
                !TryParse(interval.To, out var to) || to <= from)
            {
                throw new InvalidOperationException($"Invalid migration interval on {candidate.FullName}.");
            }
            if (!steps.TryAdd(interval.From, new(interval.From, interval.To, candidate)))
            {
                throw new InvalidOperationException($"More than one migration starts at {interval.From}.");
            }
        }
        var version = Legacy;
        var count = 0;
        while (version != Target && steps.TryGetValue(version, out var next))
        {
            version = next.To;
            count++;
        }
        if (version != Target || count != steps.Count)
        {
            throw new InvalidOperationException($"Migrations must form one complete chain from {Legacy} to {Target}.");
        }
    }

    internal string Legacy { get; }
    internal string Target { get; }
    internal IEnumerable<Type> Implementations => steps.Values.Select(step => step.Implementation);

    internal MigrationStep? GetNext(string version, string? pendingTarget)
    {
        if (version == Target && pendingTarget is null)
        {
            return null;
        }
        if (!steps.TryGetValue(version, out var step) || pendingTarget is not null && pendingTarget != step.To)
        {
            throw new InvalidOperationException($"Unsupported data migration state at {version} (pending {pendingTarget ?? "none"}, application target {Target}).");
        }
        return step;
    }

    private static bool TryParse(string value, out Version version)
    {
        if (Version.TryParse(value, out var parsed) && parsed.Build < 0 && parsed.ToString() == value)
        {
            version = parsed;
            return true;
        }
        version = null!;
        return false;
    }
}
