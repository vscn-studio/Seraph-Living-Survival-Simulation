namespace Seraph.Core.Framework;

/// <summary>Shared real-time eyelid phase for server combat and client presentation. No offline catch-up.</summary>
public class SleepEyeCycle
{
    private double openTime, phase, hold;
    private bool closing;
    public long Sequence { get; private set; }
    public SleepEyeState Capture() => new(openTime, closing ? phase : 0, hold, closing, Sequence);
    public double ClosureAfter(double seconds, double pressure)
    {
        if (!double.IsFinite(pressure) || pressure < .9) return 0;
        var delta = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, .25) : 0;
        var p = closing ? phase + delta : openTime + delta - Interval(pressure);
        return p < 0 ? 0 : Coverage(p, closing ? hold : Hold(pressure));
    }
    public bool Restore(SleepEyeState state)
    {
        if (!double.IsFinite(state.OpenTime + state.Phase + state.Hold) || state.OpenTime is < 0 or > 60.25
            || state.Phase is < 0 or > 2.31 || state.Hold is < 0 or > .700001 || state.Sequence < 0) return false;
        var before = Closure; var sequence = Sequence;
        openTime = state.OpenTime; phase = state.Phase; hold = state.Hold; closing = state.Closing; Sequence = state.Sequence;
        Closure = closing ? Coverage(phase, hold) : 0;
        JustClosed = Closure >= 1 && (before < 1 || sequence != Sequence);
        Warning = false;
        return true;
    }
    private static double Coverage(double phase, double hold) => phase < .7 ? Smooth(phase / .7)
        : phase <= .7 + hold ? 1 : 1 - Smooth((phase - .7 - hold) / .9);
    public double Closure { get; private set; }
    public bool Warning { get; private set; }
    public bool JustClosed { get; private set; }
    public void Reset() { openTime = phase = hold = Closure = 0; closing = Warning = JustClosed = false; }
    public static double Interval(double pressure) => pressure <= .95
        ? 60 - 15 * Math.Clamp((pressure - .9) / .05, 0, 1)
        : 45 - 20 * Math.Clamp((pressure - .95) / .05, 0, 1);
    public static double Hold(double pressure) => pressure <= .95
        ? .25 + .15 * Math.Clamp((pressure - .9) / .05, 0, 1)
        : .4 + .3 * Math.Clamp((pressure - .95) / .05, 0, 1);
    public void Advance(double dt, double pressure)
    {
        JustClosed = false;
        if (!double.IsFinite(pressure) || pressure < .9) { Reset(); return; }
        if (!double.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, .25);
        pressure = Math.Clamp(pressure, 0, 1);
        if (!closing)
        {
            openTime += dt;
            var remaining = Interval(pressure) - openTime;
            Warning = remaining is > 0 and <= 1.5;
            if (remaining > 0) return;
            Warning = false;
            closing = true; Sequence++; phase = -remaining; openTime = 0;
            hold = Hold(pressure);
        }
        else phase += dt;
        var previouslyClosed = Closure >= 1;
        Closure = Coverage(phase, hold);
        JustClosed = !previouslyClosed && Closure >= 1;
        if (phase >= .7 + hold + .9) { closing = false; Closure = 0; }
    }
    private static double Smooth(double value) { var t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
}

public readonly record struct SleepEyeState(double OpenTime, double Phase, double Hold, bool Closing, long Sequence);
