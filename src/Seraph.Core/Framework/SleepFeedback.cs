namespace Seraph.Core.Framework;

public sealed record SleepCue(double HomeostaticPressure, double CircadianSleepCue, bool RestRecommended);

/// <summary>Calendar is advisory only. It never supplies biological elapsed time or overrides native sleep.</summary>
public static class SleepFeedback
{
    public static SleepCue Evaluate(double pressure, double fatigue, double localCalendarHour)
    {
        if (!double.IsFinite(pressure) || !double.IsFinite(fatigue) || !double.IsFinite(localCalendarHour)
            || pressure is < 0 or > 1 || fatigue is < 0 or > 1)
            throw new ArgumentException("Invalid sleep feedback input.");
        var hour = (localCalendarHour % 24 + 24) % 24;
        // Gameplay phase seed: peak at 02:00. No measured light/chronotype adapter is assumed.
        var cue = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (hour - 2) / 24);
        return new(pressure, cue, pressure > 0.7 || fatigue > 0.6);
    }
}
