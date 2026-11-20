namespace Seraph.Core.Framework;

/// <summary>Wall-time recovery starts only after an uninterrupted pause in accepted work.</summary>
public sealed class RecoveryClock
{
    private double lastWork = double.NaN;
    public void RecordWork(double end)
    {
        if (double.IsFinite(end)) lastWork = double.IsNaN(lastWork) ? end : Math.Max(lastWork, end);
    }
    public double Seconds(double start, double end, double delay = 2)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start) return 0;
        if (double.IsNaN(lastWork)) lastWork = start;
        return Math.Clamp(end - Math.Max(start, lastWork + delay), 0, end - start);
    }
}
