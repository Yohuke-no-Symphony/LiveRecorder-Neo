namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value)
    {
        action(value);
    }
}
