namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class BandwidthBucket(TimeProvider timeProvider, long timestamp)
{
    private long updatedAt = timestamp;
    internal long Rate { get; private set; }
    internal double Tokens { get; private set; }
    internal double Capacity => Math.Max(1, Rate * 0.1);
    internal int Quantum => Rate == 0 ? 16 * 1024 : (int)Math.Max(1, Math.Min(16 * 1024, Rate * 0.05));
    internal int Available => Rate == 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, Math.Floor(Tokens));

    internal void Refill(long now)
    {
        Tokens = Math.Min(Capacity, Tokens + Math.Max(0, timeProvider.GetElapsedTime(updatedAt, now).TotalSeconds) * Rate);
        updatedAt = now;
    }

    internal void Configure(long rate, long now)
    {
        Refill(now);
        if (Rate == 0 || rate == 0)
        {
            Tokens = 0;
        }
        Rate = rate;
        Tokens = Math.Min(Tokens, Capacity);
    }

    internal void Take(int bytes)
    {
        if (Rate > 0)
        {
            Tokens -= bytes;
        }
    }

    internal void Refund(int bytes)
    {
        if (Rate > 0)
        {
            Tokens = Math.Min(Capacity, Tokens + bytes);
        }
    }
}
