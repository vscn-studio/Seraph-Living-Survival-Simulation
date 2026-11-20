namespace Seraph.Core.Simulation;

/// <summary>Accepted work on the real clock; calendar settlement uses its highest overlapping tier.</summary>
public sealed class ActionEnergyBudget
{
    private readonly List<(double Start, double End, double Watts)> work = [];
    public IEnumerable<double> Boundaries(double start, double end) => work
        .Where(x => x.Start < end && x.End > start).SelectMany(x => new[] { Math.Max(start, x.Start), Math.Min(end, x.End) });
    public void Record(double watts, double start, double end)
    {
        if (double.IsFinite(watts) && double.IsFinite(start) && double.IsFinite(end) && watts > 0 && end > start)
        {
            for (var i = work.Count - 1; i >= 0; i--)
                if (work[i].Watts == watts && work[i].Start <= end && work[i].End >= start)
                { start = Math.Min(start, work[i].Start); end = Math.Max(end, work[i].End); work.RemoveAt(i); }
            work.Add((start, end, watts));
        }
    }
    public double Consume(double start, double end, double baseline = 0)
    {
        if (end <= start) return baseline;
        var boundaries = new List<double> { start, end };
        foreach (var span in work)
        {
            if (span.End <= start || span.Start >= end) continue;
            boundaries.Add(Math.Max(start, span.Start)); boundaries.Add(Math.Min(end, span.End));
        }
        boundaries.Sort();
        var energy = 0d;
        for (var i = 1; i < boundaries.Count; i++)
        {
            var a = boundaries[i - 1]; var b = boundaries[i];
            var power = baseline;
            foreach (var span in work)
                if (span.Start < b && span.End > a) power = Math.Max(power, span.Watts);
            energy += (b - a) * power;
        }
        work.RemoveAll(x => x.End <= end);
        return energy / (end - start);
    }
}
