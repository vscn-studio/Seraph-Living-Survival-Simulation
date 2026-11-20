namespace Seraph.Application;

/// <summary>Recent observed values only. Time is active client seconds, never calendar or offline time.</summary>
public sealed class MetricTrends
{
    private sealed class Series(double minimum, double maximum)
    {
        internal readonly double Minimum = minimum, Maximum = maximum;
        internal readonly Queue<(double Time, double Value)> Samples = new();
    }
    private readonly Dictionary<string, Series> series = [];
    public double Time { get; private set; }
    public long Revision { get; private set; }
    public void Advance(double seconds)
    { if (double.IsFinite(seconds) && seconds > 0) Time += Math.Min(seconds, .25); }
    public void Clear() { series.Clear(); Time = 0; Revision++; }
    public void Record(string key, double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        { series.Remove(key); return; }
        if (!series.TryGetValue(key, out var data) || data.Minimum != minimum || data.Maximum != maximum)
            series[key] = data = new(minimum, maximum);
        data.Samples.Enqueue((Time, value));
        while (data.Samples.Count > 1 && (data.Samples.Peek().Time < Time - 5 || data.Samples.Count > 27)) data.Samples.Dequeue();
        Revision++;
    }
    public (double Old, double Current, int Direction) Get(string key, double precision = double.NaN)
    {
        if (!series.TryGetValue(key, out var data) || data.Samples.Count == 0 || Time - data.Samples.Last().Time > .5) return default;
        var old = data.Samples.Peek().Value; var current = data.Samples.Last().Value;
        if (double.IsNaN(precision)) precision = (data.Maximum - data.Minimum) * .001;
        var direction = Math.Abs(current - old) < precision ? 0 : Math.Sign(current - old);
        var range = data.Maximum - data.Minimum;
        return (Math.Clamp((old - data.Minimum) / range, 0, 1), Math.Clamp((current - data.Minimum) / range, 0, 1), direction);
    }
}
