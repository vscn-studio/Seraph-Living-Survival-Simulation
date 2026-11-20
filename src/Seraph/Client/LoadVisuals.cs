namespace Seraph.Client;

internal readonly record struct LoadVisuals(double Edge, double Center, double Blur, double Lid = 0, double Ghost = 0)
{
    private static readonly (double X, double Y)[] FatigueEdge = [(0, 0), (.6, 0), (.8, .04), (.9, .07), (1, .10)];
    private static readonly (double X, double Y)[] SleepEdge = [(0, 0), (.7, 0), (.8, .08), (.9, .16), (.95, .24), (1, .32)];
    private static readonly (double X, double Y)[] SleepCenter = [(0, 0), (.8, 0), (.9, .01), (.95, .02), (1, .04)];
    private static readonly (double X, double Y)[] SleepBlur = [(0, 0), (.7, 0), (.8, .06), (.9, .18), (.95, .32), (1, .5)];
    private static readonly (double X, double Y)[] SleepLid = [(0, 0), (.75, 0), (.85, .06), (.9, .12), (.95, .20), (1, .32)];
    private static readonly (double X, double Y)[] SleepGhost = [(0, 0), (.85, 0), (.9, .025), (.95, .055), (1, .09)];
    internal static LoadVisuals Evaluate(double fatigue, double pressure)
    {
        var fatigueEdge = Curve(fatigue, FatigueEdge);
        var edge = Curve(pressure, SleepEdge);
        var center = Curve(pressure, SleepCenter);
        var blur = Curve(pressure, SleepBlur);
        return new(Math.Max(fatigueEdge, edge), center, blur, Curve(pressure, SleepLid), Curve(pressure, SleepGhost));
    }
    private static double Curve(double value, (double X, double Y)[] points)
    {
        if (!double.IsFinite(value)) return 0;
        value = Math.Clamp(value, 0, 1);
        for (var i = 1; i < points.Length; i++)
        {
            if (value > points[i].X) continue;
            var t = (value - points[i - 1].X) / (points[i].X - points[i - 1].X);
            t *= t * (3 - 2 * t);
            return points[i - 1].Y + (points[i].Y - points[i - 1].Y) * t;
        }
        return points[^1].Y;
    }
    internal LoadVisuals Approach(LoadVisuals target, double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0) return this;
        dt = Math.Min(dt, .25);
        double Move(double current, double value, double maximum)
        {
            var change = maximum * dt / (value > current ? .8 : 2);
            return Math.Abs(value - current) <= change + 1e-12 ? value : current + Math.Sign(value - current) * change;
        }
        return new(Move(Edge, target.Edge, .32), Move(Center, target.Center, .04), Move(Blur, target.Blur, .5),
            Move(Lid, target.Lid, .32), Move(Ghost, target.Ghost, .09));
    }
}
