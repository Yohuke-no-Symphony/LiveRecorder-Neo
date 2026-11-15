using System.Text;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed record WorkerServiceInvocation
{
    internal required string ExecutablePath { get; init; }
    internal required IReadOnlyList<string> Arguments { get; init; }
    internal required string WorkingDirectory { get; init; }

    internal static WorkerServiceInvocation Current(string configurationPath)
    {
        var executable = Environment.ProcessPath
            ?? throw new WorkerServiceException("Unable to determine the current worker executable path.");
        var application = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Environment.GetCommandLineArgs()[0]) : null;
        if (!File.Exists(executable) || application is not null && !File.Exists(application))
        {
            throw new WorkerServiceException("The worker executable or its application DLL does not exist.");
        }
        return Create(executable, application, configurationPath);
    }

    internal static WorkerServiceInvocation Create(string executablePath, string? applicationPath, string configurationPath)
    {
        var executable = Path.GetFullPath(executablePath);
        var configuration = Path.GetFullPath(configurationPath);
        var arguments = new List<string>();
        if (applicationPath is not null)
        {
            arguments.Add(Path.GetFullPath(applicationPath));
        }
        arguments.AddRange(["run", "--config", configuration]);
        var directory = Path.GetDirectoryName(applicationPath is null ? executable : arguments[0])!;
        foreach (var value in arguments.Prepend(executable).Append(directory))
        {
            if (value.Any(char.IsControl))
            {
                throw new WorkerServiceException("Service paths cannot contain control characters.");
            }
        }
        return new() { ExecutablePath = executable, Arguments = arguments, WorkingDirectory = directory };
    }

    internal string WindowsCommandLine() => string.Join(' ', Arguments.Prepend(ExecutablePath).Select(QuoteWindowsArgument));

    internal static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
