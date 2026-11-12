namespace Yohuke.LiveRecorder.Media;

/// <summary>Signals that the recording volume has reached its configured safety reserve.</summary>
public sealed class InsufficientMediaSpaceException : IOException
{
    public long AvailableBytes { get; }
    public long RequiredBytes { get; }

    /// <summary>Creates a disk-reserve error for the recording supervisor.</summary>
    public InsufficientMediaSpaceException(long availableBytes, long requiredBytes)
        : base($"The recording volume has {availableBytes} available bytes, below its {requiredBytes}-byte safety reserve.")
    {
        AvailableBytes = availableBytes;
        RequiredBytes = requiredBytes;
    }
}
