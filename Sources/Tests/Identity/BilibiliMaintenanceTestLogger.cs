using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class BilibiliMaintenanceTestLogger : ILogger<BilibiliService>
{
    internal List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
