namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>原生记录上传和时间校准的统一边界。</summary>
public static class LiveMarkerLimits
{
    public const int MAXIMUM_BYTES = 5 * 1024 * 1024;
    public const int MAXIMUM_ENTRIES = 20000;
    public const long MAXIMUM_CALIBRATION_MS = 315537897599999;
}
