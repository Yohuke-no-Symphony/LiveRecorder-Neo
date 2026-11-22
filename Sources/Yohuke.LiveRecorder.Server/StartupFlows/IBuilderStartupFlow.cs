namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal interface IBuilderStartupFlow
{
    void Execute(WebApplicationBuilder builder);
}
