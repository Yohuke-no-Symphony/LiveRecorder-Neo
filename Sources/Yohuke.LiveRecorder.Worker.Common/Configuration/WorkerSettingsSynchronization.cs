using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerSettingsSynchronization
{
    internal static WorkerHello? CreateUpdate(WorkerSettings settings, WorkerWelcome welcome, string kind = "Transcoding")
    {
        var configurationHash = Hash(settings.Configuration);
        if (configurationHash == Hash(welcome.Configuration))
        {
            return null;
        }
        var sameBinding = settings.BindingHash == BindingHash(settings.Worker) && settings.ConfigurationHash is not null;
        if (sameBinding ? settings.ConfigurationHash == configurationHash : settings.Configuration == new WorkerConfiguration())
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

    internal static WorkerSettings Accept(WorkerSettings settings, WorkerConfiguration configuration, int revision) => settings with
    {
        Configuration = configuration with { },
        ConfigurationRevision = revision,
        ConfigurationHash = Hash(configuration),
        BindingHash = BindingHash(settings.Worker)
    };

    private static string BindingHash(WorkerConnectionOptions options) => Hash(new
    {
        options.ApiBaseUrl, options.ControlHost, options.ControlPort, options.WorkerId, options.TlsServerName
    });

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
