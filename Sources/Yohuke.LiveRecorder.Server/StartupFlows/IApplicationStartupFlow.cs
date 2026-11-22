namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal interface IApplicationStartupFlow
{
    void Execute(WebApplication app);
}
