namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal static class StartupFlowRunner
{
    internal static WebApplication Run(
        WebApplicationBuilder builder,
        IReadOnlyList<IBuilderStartupFlow> builderFlows,
        IReadOnlyList<IApplicationStartupFlow> applicationFlows)
    {
        foreach (var flow in builderFlows)
        {
            flow.Execute(builder);
        }

        var app = builder.Build();

        foreach (var flow in applicationFlows)
        {
            flow.Execute(app);
        }

        return app;
    }
}
