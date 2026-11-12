namespace Yohuke.LiveRecorder.Media;

/// <summary>
/// Reports an unsuccessful media process without treating partial output as success.
/// </summary>
public sealed class MediaToolException : IOException
{
    public int ExitCode { get; }

    /// <summary>
    /// Creates an error for an external media tool failure.
    /// </summary>
    public MediaToolException(string tool, int exitCode, string diagnostic)
        : base($"{Path.GetFileName(tool)} exited with code {exitCode}: {diagnostic}")
    {
        ExitCode = exitCode;
    }
}
