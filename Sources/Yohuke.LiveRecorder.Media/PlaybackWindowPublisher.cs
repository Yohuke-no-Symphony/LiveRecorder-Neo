namespace Yohuke.LiveRecorder.Media;

internal static class PlaybackWindowPublisher
{
    internal static void Publish(string temporaryDirectory, string outputDirectory)
    {
        Directory.Move(temporaryDirectory, outputDirectory);
    }
}
