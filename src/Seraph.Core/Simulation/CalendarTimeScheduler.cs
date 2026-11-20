namespace Seraph.Core.Simulation;

/// <summary>One model day is 86400 seconds, irrespective of calendar day length.
/// The adapter resets the baseline on pause, unload and explicit clock changes.
/// A round consumes at most two normalized game minutes; debt is never truncated.</summary>
public sealed class CalendarTimeScheduler
{
    public const double SecondsPerDay = 86400;
    public const double DefaultCalendarRate = 30;
    private double pending;
    public double PendingSeconds => pending;
    public TimeBudgetPlan Schedule(double normalizedSeconds, bool paused = false)
    {
        if (!double.IsFinite(normalizedSeconds) || normalizedSeconds < 0)
            return new(false, 0, 0, 0, 0, pending, "Invalid calendar interval.");
        if (paused) return new(true, 0, 0, 0, 0, pending, null);
        pending += normalizedSeconds;
        var accepted = Math.Min(pending, 120);
        var count = (int)Math.Ceiling(accepted / 8);
        pending = Math.Max(0, pending - accepted);
        return new(true, count, count == 0 ? 0 : accepted / count, accepted, 0, pending, null);
    }
    public void Reset() => pending = 0;
}
