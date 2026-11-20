namespace Seraph.Core.Framework;

/// <summary>Union of accepted real-time work intervals, consumed once by the recovery clock.</summary>
public sealed class WorkIntervals
{
    private readonly List<(double Start, double End)> intervals = [];
    public IEnumerable<double> Boundaries(double start, double end) => intervals
        .Where(x => x.Start < end && x.End > start).SelectMany(x => new[] { Math.Max(start, x.Start), Math.Min(end, x.End) });
    public void Add(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start) return;
        // Coalesce on insertion: high-frequency native hooks cannot grow this ledger without bound.
        for (var i = intervals.Count - 1; i >= 0; i--)
            if (intervals[i].Start <= end && intervals[i].End >= start)
            { start = Math.Min(start, intervals[i].Start); end = Math.Max(end, intervals[i].End); intervals.RemoveAt(i); }
        intervals.Add((start, end));
    }
    public double RestSeconds(double start, double end)
    {
        var work = 0d; var cursor = start;
        foreach (var span in intervals.OrderBy(x => x.Start))
        {
            var a = Math.Max(cursor, span.Start); var b = Math.Min(end, span.End);
            if (b <= a) continue;
            work += b - a; cursor = b;
        }
        intervals.RemoveAll(x => x.End <= end);
        return Math.Max(0, end - start - work);
    }
}
