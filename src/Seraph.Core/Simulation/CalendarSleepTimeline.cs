namespace Seraph.Core.Simulation;

public readonly record struct CalendarSleepPeriod(double Hours, bool Sleeping, double BedQuality);

/// <summary>Retains sleep/wake boundaries between calendar settlement callbacks.</summary>
public sealed class CalendarSleepTimeline
{
    private readonly Queue<(double Hours, bool Sleeping, double Quality)> changes = new();
    private bool sleeping;
    private double quality = 1;

    public void Reset(bool isSleeping, double bedQuality)
    {
        changes.Clear();
        sleeping = isSleeping;
        quality = bedQuality;
    }

    public void Observe(double hours, bool isSleeping, double bedQuality)
    {
        if (!double.IsFinite(hours)) return;
        var last = changes.Count > 0 ? changes.Last() : (hours, sleeping, quality);
        if (last.Item2 == isSleeping && last.Item3 == bedQuality) return;
        changes.Enqueue((hours, isSleeping, bedQuality));
    }

    public IReadOnlyList<CalendarSleepPeriod> Consume(double startHours, double endHours)
    {
        if (!double.IsFinite(startHours) || !double.IsFinite(endHours) || endHours < startHours)
            throw new ArgumentOutOfRangeException(nameof(endHours));
        var periods = new List<CalendarSleepPeriod>();
        var cursor = startHours;
        while (changes.TryPeek(out var change) && change.Hours <= endHours)
        {
            changes.Dequeue();
            if (change.Hours > cursor)
                periods.Add(new(change.Hours - cursor, sleeping, quality));
            cursor = Math.Max(cursor, change.Hours);
            sleeping = change.Sleeping;
            quality = change.Quality;
        }
        if (endHours > cursor) periods.Add(new(endHours - cursor, sleeping, quality));
        return periods;
    }
}
