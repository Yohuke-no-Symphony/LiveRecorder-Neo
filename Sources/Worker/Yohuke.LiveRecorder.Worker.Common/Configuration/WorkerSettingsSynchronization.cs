using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerSettingsSynchronization
{
    internal static WorkerHello? CreateUpdate(WorkerSettings settings, WorkerWelcome welcome, string kind = "Transcoding")
    {
        settings = NormalizeLoaded(settings, kind);
        var configurationHash = WorkerConfigurationProjection.ComputeHash(settings.Configuration, kind);
        if (configurationHash == WorkerConfigurationProjection.ComputeHash(welcome.Configuration, kind))
        {
            return null;
        }
        var sameBinding = settings.BindingHash == BindingHash(settings.Worker) && settings.ConfigurationHash is not null;
        if (sameBinding ? settings.ConfigurationHash == configurationHash
            : configurationHash == WorkerConfigurationProjection.ComputeHash(WorkerConfigurationProjection.Default(kind), kind))
        {
            return null;
        }
        WorkerConfigurationValidation.Validate(settings.Configuration, kind);
        if (sameBinding && settings.ConfigurationRevision != welcome.ConfigurationRevision)
        {
            throw new InvalidOperationException("Local and server settings have both changed. Run 'config' to review the latest server configuration and resolve the version conflict.");
        }
        return new()
        {
            Kind = kind,
            WorkerId = settings.Worker.WorkerId,
            Secret = settings.Worker.Secret,
            InitialConfiguration = settings.Configuration with { },
            ExpectedConfigurationRevision = welcome.ConfigurationRevision
        };
    }

    internal static WorkerSettings Accept(WorkerSettings settings, WorkerConfiguration configuration, int revision,
        string kind = "Transcoding") => settings with
    {
        Configuration = WorkerConfigurationProjection.Normalize(configuration, kind),
        ConfigurationRevision = revision,
        ConfigurationHash = WorkerConfigurationProjection.ComputeHash(configuration, kind),
        BindingHash = BindingHash(settings.Worker)
    };

    internal static WorkerSettings NormalizeLoaded(WorkerSettings settings, string kind)
    {
        var hash = settings.ConfigurationHash;
        if (hash is not null && (hash == Hash(settings.Configuration)
                || hash == WorkerConfigurationProjection.ComputeHash(settings.Configuration, "Transcoding")))
        {
            hash = WorkerConfigurationProjection.ComputeHash(settings.Configuration, kind);
        }
        return settings with
        {
            Configuration = WorkerConfigurationProjection.Normalize(settings.Configuration, kind),
            ConfigurationHash = hash
        };
    }

    private static string BindingHash(WorkerConnectionOptions options) => Hash(new
    {
        options.ApiBaseUrl, options.ControlHost, options.ControlPort, options.WorkerId, options.TlsServerName
    });

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
