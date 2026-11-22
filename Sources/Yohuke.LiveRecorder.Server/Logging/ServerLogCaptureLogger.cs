using System.Globalization;
using System.Text.RegularExpressions;

namespace Yohuke.LiveRecorder.Server.Logging;

internal sealed class ServerLogCaptureLogger(ServerLogBuffer logs, string category, LogLevel minimumLevel) : ILogger
{
    private const int MAX_MESSAGE_LENGTH = 2048;
    private const int MAX_FIELD_LENGTH = 128;
    private const string ORIGINAL_FORMAT = "{OriginalFormat}";
    private const string INSTALLATION_TOKEN = "InstallationToken";
    private const string HIDDEN_VALUE = "…";

    private static Regex MessageTokens { get; } = new(@"\{\{|\}\}|\{(?<name>[^{}:,]+)(?:,[^{}:]*)?(?::[^{}]*)?\}", RegexOptions.NonBacktracking);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel <= LogLevel.Critical;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            return;
        }

        string? template = null;
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, INSTALLATION_TOKEN, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (field.Key == ORIGINAL_FORMAT)
            {
                template = field.Value as string;
            }
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        var message = MessageTokens.Replace(LimitMessage(template), match =>
        {
            var field = match.Groups["name"];
            return field.Success ? ReadField(fields, field.Value) : match.Value[..1];
        });
        logs.Append(logLevel, category, LimitMessage(message), exception?.GetType().Name);
    }

    private static string ReadField(IReadOnlyList<KeyValuePair<string, object?>> fields, string name)
    {
        if (name is not ("RoomId" or "SessionId" or "JobId" or "TaskId" or "WorkerId" or "ConnectionId" or "RequestId" or "TraceIdentifier" or "ErrorType"))
        {
            return HIDDEN_VALUE;
        }

        foreach (var field in fields)
        {
            if (field.Key != name)
            {
                continue;
            }

            var value = field.Value switch
            {
                string text => text,
                Guid id => id.ToString("D"),
                byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(field.Value, CultureInfo.InvariantCulture),
                _ => null
            };
            return value is { Length: > 0 and <= MAX_FIELD_LENGTH }
                && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
                ? value
                : HIDDEN_VALUE;
        }

        return HIDDEN_VALUE;
    }

    private static string LimitMessage(string message)
    {
        if (message.Length <= MAX_MESSAGE_LENGTH)
        {
            return message;
        }

        var prefixLength = MAX_MESSAGE_LENGTH - (char.IsHighSurrogate(message[MAX_MESSAGE_LENGTH - 2]) ? 2 : 1);
        return message[..prefixLength] + HIDDEN_VALUE;
    }
}
