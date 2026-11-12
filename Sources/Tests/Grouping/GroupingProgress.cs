namespace Yohuke.LiveRecorder.Grouping.Tests;

internal sealed class GroupingProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
