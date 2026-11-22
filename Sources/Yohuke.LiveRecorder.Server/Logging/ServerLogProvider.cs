using Microsoft.Extensions.Logging.Abstractions;

namespace Yohuke.LiveRecorder.Server.Logging;

internal sealed class ServerLogProvider(ServerLogBuffer logs, string applicationCategory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        if (categoryName == applicationCategory || categoryName == "Microsoft.Hosting.Lifetime"
            || categoryName.StartsWith("Yohuke.LiveRecorder.", StringComparison.Ordinal))
        {
            return new ServerLogCaptureLogger(logs, categoryName, LogLevel.Trace);
        }

        if (categoryName.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
            && !categoryName.StartsWith("Microsoft.AspNetCore.HttpLogging.", StringComparison.Ordinal))
        {
            return new ServerLogCaptureLogger(logs, categoryName, LogLevel.Warning);
        }

        return NullLogger.Instance;
    }

    public void Dispose()
    {
    }
}
