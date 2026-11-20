namespace Seraph.Core.Framework;

/// <summary>Native elapsed-use clocks are cumulative; repeats never earn work twice.</summary>
public sealed class HeldWorkLedger
{
    private readonly Dictionary<string, double> elapsed = new(StringComparer.Ordinal);
    public double Advance(string source, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return 0;
        var before = elapsed.GetValueOrDefault(source);
        elapsed[source] = Math.Max(before, seconds);
        return Math.Max(0, seconds - before);
    }
    // A final stop/cancel may follow a false completion step. Only finish an established
    // native use, and bound the final remainder to one ordinary callback interval.
    public double Finish(string source, double seconds)
        => elapsed.ContainsKey(source) ? Math.Min(.5, Advance(source, seconds)) : 0;
    public void End(string source) => elapsed.Remove(source);
}
