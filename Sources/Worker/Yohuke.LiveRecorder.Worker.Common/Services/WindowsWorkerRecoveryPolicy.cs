namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed class WindowsWorkerRecoveryPolicy(string serviceName)
{
    internal async Task ApplyAsync(Func<IReadOnlyList<string>, Task> execute)
    {
        await execute(["failure", serviceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000"]);
        await execute(["failureflag", serviceName, "1"]);
    }
}
