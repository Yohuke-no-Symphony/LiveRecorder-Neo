namespace Yohuke.LiveRecorder.Media.Tests;

internal static class FlvSampleEditor
{
    internal static byte[] AddTimestampJump(byte[] source)
    {
        var output = source.ToArray();
        var offset = 13;
        while (offset + 11 <= output.Length)
        {
            var size = (output[offset + 1] << 16) | (output[offset + 2] << 8) | output[offset + 3];
            var timestamp = (output[offset + 7] << 24) | (output[offset + 4] << 16) | (output[offset + 5] << 8) | output[offset + 6];
            if (output[offset] is 8 or 9 && timestamp >= 2000)
            {
                timestamp += 30000;
                output[offset + 4] = (byte)(timestamp >> 16);
                output[offset + 5] = (byte)(timestamp >> 8);
                output[offset + 6] = (byte)timestamp;
                output[offset + 7] = (byte)(timestamp >> 24);
            }
            offset += 11 + size + 4;
        }
        return output;
    }
}
