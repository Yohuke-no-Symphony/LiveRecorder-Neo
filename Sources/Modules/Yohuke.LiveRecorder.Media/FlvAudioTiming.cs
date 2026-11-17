using BililiveRecorder.Flv;

namespace Yohuke.LiveRecorder.Media;

internal static class FlvAudioTiming
{
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    internal static double? FrameDurationMs(Tag tag)
    {
        if (tag.Type != TagType.Audio || !tag.IsHeader() || tag.BinaryData is not { Length: >= 4 } binary)
        {
            return null;
        }
        var data = binary.GetBuffer();
        var index = ((data[2] & 7) << 1) | (data[3] >> 7);
        var sampleRate = index < SampleRates.Length ? SampleRates[index]
            : index == 15 && binary.Length >= 7
                ? ((data[3] & 127) << 17) | (data[4] << 9) | (data[5] << 1) | (data[6] >> 7) : 0;
        if (sampleRate <= 0)
        {
            return null;
        }
        var samples = (data[index == 15 ? 6 : 3] & 4) != 0 ? 960 : 1024;
        return samples * 1000d / sampleRate;
    }
}
