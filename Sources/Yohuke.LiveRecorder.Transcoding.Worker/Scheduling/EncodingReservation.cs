using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;

internal sealed class EncodingReservation(EncoderCapability encoder, int threads, Action release) : IDisposable
{
    private Action? releaseAction = release;
    internal EncoderCapability Encoder { get; } = encoder;
    internal int Threads { get; } = threads;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
}
