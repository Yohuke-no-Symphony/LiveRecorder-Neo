using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Runs kernel Job Object integration only on Windows.</summary>
public sealed class WindowsRecoveryFactAttribute : FactAttribute
{
    /// <summary>Skips native Windows process lifetime checks on other systems.</summary>
    public WindowsRecoveryFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows Job Objects require Windows.";
        }
    }
}
