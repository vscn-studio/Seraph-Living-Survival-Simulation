namespace Seraph.Core.Framework;

/// <summary>Relative gameplay dose in baseline breathing seconds, never a medical mass concentration.</summary>
public static class SmokeInhalation
{
    public const double MaximumDose = 3600;
    public static double Ventilation(double activityPowerW) => 8 + 32 * Math.Clamp(activityPowerW / 600, 0, 1);
    public static double Advance(double dose, double concentration, double litersPerMinute, double activeSeconds)
    {
        if (!double.IsFinite(dose) || dose < 0 || dose > MaximumDose || !double.IsFinite(concentration)
            || concentration < 0 || !double.IsFinite(litersPerMinute) || litersPerMinute < 0
            || !double.IsFinite(activeSeconds) || activeSeconds < 0) throw new ArgumentOutOfRangeException(nameof(dose));
        var dt = Math.Min(activeSeconds, .5);
        // Clearance is a clean-air rule; exposure is charged once using actual active real time.
        return concentration > 0 ? Math.Min(MaximumDose, dose + concentration * litersPerMinute / 8 * dt)
            : dose * Math.Exp(-Math.Log(2) * dt / 180);
    }
    public static double Capacity(double dose) => 1 - .25 * Math.Clamp((dose - 60) / 240, 0, 1);
    public static double Severity(double dose) => dose >= 60 ? Math.Clamp(dose / 600, .1, 1) : 0;
    public static double DamagePerRealSecond(double dose) => Math.Clamp((dose - 300) / 300, 0, 1) / 60;
}
