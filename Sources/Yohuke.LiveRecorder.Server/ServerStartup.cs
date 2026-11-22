using Yohuke.LiveRecorder.Server.StartupFlows;

namespace Yohuke.LiveRecorder.Server;

/// <summary>服务组合入口，可由原生宿主与集成测试创建。</summary>
public static class ServerStartup
{
    /// <summary>构建安装、鉴权、后台录制与媒体交付管线。</summary>
    public static WebApplication CreateApplication(string[] args)
    {
        var (builder, developmentOrigins) = HostStartupFlow.Prepare(args);
        var (webRoot, currentStorage) = InstanceStorageStartupFlow.Prepare(builder);

        return StartupFlowRunner.Run(
            builder,
            [
                new ServiceRegistrationStartupFlow(),
                new AuthenticationStartupFlow(currentStorage),
                new HttpPolicyStartupFlow()
            ],
            [
                new RequestDiagnosticsStartupFlow(),
                new RequestPipelineStartupFlow(currentStorage, developmentOrigins),
                new EndpointMappingStartupFlow(developmentOrigins),
                new WebUiStartupFlow(webRoot),
                new InstallationNoticeStartupFlow()
            ]);
    }
}
