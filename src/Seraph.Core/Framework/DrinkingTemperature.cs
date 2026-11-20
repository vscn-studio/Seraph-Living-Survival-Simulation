namespace Seraph.Core.Framework;

/// <summary>Temperature response for water consumed by a player.</summary>
public static class DrinkingTemperature
{
    public const double MinimumC = -5;
    public const double MaximumC = 50;

    public static bool IsDrinkable(double temperatureC)
        => double.IsFinite(temperatureC) && temperatureC is >= MinimumC and <= MaximumC;

    public static double AbsorptionMultiplier(double temperatureC)
        => IsDrinkable(temperatureC) ? 1 : 0;

    /// <summary>Short-lived thermal stimulus in degrees C, positive for heat.</summary>
    public static double ThermalStimulusC(double temperatureC) => 0;
}
