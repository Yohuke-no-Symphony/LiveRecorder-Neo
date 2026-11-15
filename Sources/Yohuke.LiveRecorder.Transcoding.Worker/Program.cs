using System.Text.Json;
using System.Runtime.InteropServices;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Transcoding.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            Console.WriteLine("转码 Worker：[--config <settings.json>] [--bind | --configure | --check]\n--bind：在终端配置主站地址、Worker ID 和 Secret，保存后退出\n--configure：配置连接信息和运行参数，保存并同步主站后退出\n--check：同步设置、验证主站配置和本机编码能力后退出\n默认使用可执行文件旁的 settings.json；首次启动进入终端绑定向导\n不带动作参数：连接主站并持续处理任务，Ctrl+C 安全停止");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
            var bind = false;
            var configure = false;
            var check = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--config" when index + 1 < args.Length:
                        path = Path.GetFullPath(args[++index]);
                        break;
                    case "--configure":
                        configure = true;
                        break;
                    case "--bind":
                        bind = true;
                        break;
                    case "--check":
                        check = true;
                        break;
                    default:
                        throw new ArgumentException("无法识别的启动参数：" + args[index]);
                }
            }
            if (bind && (configure || check))
            {
                throw new ArgumentException("--bind 请单独使用；完整配置使用 --configure");
            }
            using var settings = new WorkerSettingsStore(path);
            await settings.LoadAsync(cancellation.Token);
            if (bind || configure || string.IsNullOrWhiteSpace(settings.Current.Worker.WorkerId)
                || string.IsNullOrWhiteSpace(settings.Current.Worker.Secret))
            {
                var configured = WorkerSetup.ConfigureConnection(settings.Current.Worker);
                configured.Validate();
                await settings.SaveAsync(settings.Current with { Worker = configured }, cancellation.Token);
                Console.WriteLine("Worker 绑定设置已保存：" + settings.Path);
                if (bind)
                {
                    return 0;
                }
            }
            var options = settings.Current.Worker;
            options.Validate();
            WorkerWelcome welcome;
            await using (var connection = await WorkerControlClient.ConnectAsync(options, cancellation.Token))
            {
                welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                {
                    WorkerId = options.WorkerId,
                    Secret = options.Secret
                }, cancellation.Token);
            }
            if (configure || string.IsNullOrWhiteSpace(welcome.Configuration.TempDirectory)
                && settings.Current.Configuration == new WorkerConfiguration())
            {
                var configured = WorkerSetup.Configure(welcome.Configuration);
                WorkerSetup.Validate(configured);
                var draft = WorkerSettingsSynchronization.Accept(settings.Current, welcome.Configuration, welcome.ConfigurationRevision)
                    with { Configuration = configured };
                await settings.SaveAsync(draft, cancellation.Token);
            }
            var update = WorkerSettingsSynchronization.CreateUpdate(settings.Current, welcome);
            if (update is not null)
            {
                await using var connection = await WorkerControlClient.ConnectAsync(options, cancellation.Token);
                welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", update, cancellation.Token);
            }
            await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellation.Token);
            if (configure && !check)
            {
                Console.WriteLine("Worker 完整设置已保存并同步主站：" + settings.Path);
                return 0;
            }
            if (check)
            {
                WorkerSetup.Validate(welcome.Configuration);
                var capabilities = await new EncoderProbe(new WorkerProcessRunner()).ProbeAsync(welcome.Configuration, cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(capabilities, new JsonSerializerOptions(WorkerProtocol.JsonOptions) { WriteIndented = true }));
                return capabilities.Encoders.Any(encoder => encoder.Available) ? 0 : 1;
            }
            await new WorkerRuntime(options, settings).RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Worker 启动或运行失败：" + exception.Message);
            return 1;
        }
    }
}
