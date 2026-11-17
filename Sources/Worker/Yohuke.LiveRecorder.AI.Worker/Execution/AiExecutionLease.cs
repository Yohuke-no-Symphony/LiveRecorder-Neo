namespace Yohuke.LiveRecorder.AI.Worker.Execution;

internal sealed class AiExecutionLease(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock sync = new();
    private long renewedAt;
    private TimeSpan duration;
    private bool connected;
    private bool reportOnly;

    internal bool ReportOnly
    {
        get
        {
            lock (sync)
            {
                return reportOnly;
            }
        }
    }

    internal bool CanReport
    {
        get
        {
            lock (sync)
            {
                return connected && clock.GetElapsedTime(renewedAt) < duration;
            }
        }
    }

    internal void Renew(int seconds, bool onlyReport = false)
    {
        lock (sync)
        {
            renewedAt = clock.GetTimestamp();
            duration = TimeSpan.FromSeconds(Math.Max(1, seconds));
            connected = true;
            reportOnly = reportOnly || onlyReport;
        }
    }

    internal void Disconnect()
    {
        lock (sync)
        {
            connected = false;
        }
    }

    internal void RequireModelCall()
    {
        if (!CanReport || ReportOnly)
        {
            throw new AiExecutionPausedException();
        }
    }
}
