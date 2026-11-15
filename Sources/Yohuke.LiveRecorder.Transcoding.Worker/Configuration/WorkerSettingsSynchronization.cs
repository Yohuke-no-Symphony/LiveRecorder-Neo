using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerSettingsSynchronization
{
    internal static WorkerHello? CreateUpdate(WorkerSettings settings, WorkerWelcome welcome)
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
        WorkerSetup.Validate(settings.Configuration);
        if (sameBinding && settings.ConfigurationRevision != welcome.ConfigurationRevision)
        {
            throw new InvalidOperationException("本地设置与主站配置均已修改，未覆盖任何一方。请运行 --configure，以主站最新配置为基础重新确认运行参数");
        }
        return new()
        {
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
