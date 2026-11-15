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
            Console.WriteLine("转码 Worker：--config <appsettings.json> [--configure | --check]\n--configure：在终端修改主站保存的运行配置\n--check：验证主站配置和本机编码能力后退出\n不带动作参数：连接主站并持续处理任务，Ctrl+C 安全停止");
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
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
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
                    case "--check":
                        check = true;
                        break;
                    default:
                        throw new ArgumentException("无法识别的启动参数：" + args[index]);
                }
            }
            var settings = JsonSerializer.Deserialize<WorkerAppSettings>(await File.ReadAllTextAsync(path, cancellation.Token),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("启动配置为空");
            var options = settings.Worker;
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
            if (configure || string.IsNullOrWhiteSpace(welcome.Configuration.TempDirectory))
            {
                var configured = WorkerSetup.Configure(welcome.Configuration);
                WorkerSetup.Validate(configured);
                await using var connection = await WorkerControlClient.ConnectAsync(options, cancellation.Token);
                welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                {
                    WorkerId = options.WorkerId,
                    Secret = options.Secret,
                    InitialConfiguration = configured,
                    ExpectedConfigurationRevision = welcome.ConfigurationRevision
                }, cancellation.Token);
                Console.WriteLine("Worker 运行配置已保存到主站。");
                if (configure && !check)
                {
                    return 0;
                }
            }
            if (check)
            {
                WorkerSetup.Validate(welcome.Configuration);
                var capabilities = await new EncoderProbe(new WorkerProcessRunner()).ProbeAsync(welcome.Configuration, cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(capabilities, new JsonSerializerOptions(WorkerProtocol.JsonOptions) { WriteIndented = true }));
                return capabilities.Encoders.Any(encoder => encoder.Available) ? 0 : 1;
            }
            await new WorkerRuntime(options).RunAsync(cancellation.Token);
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
