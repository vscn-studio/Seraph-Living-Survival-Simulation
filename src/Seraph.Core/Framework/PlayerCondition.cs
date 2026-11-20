using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Distinct energy compartments. Only StoredKJ is additive; it is not an endurance score.</summary>
public readonly record struct EnergyReserves(double GlycogenKJ, double FatKJ, double PendingKJ, double GlycogenFraction)
{
    public double StoredKJ => GlycogenKJ + FatKJ;
    public static EnergyReserves From(BodyState body, BodyProfile profile) => new(body.GlycogenEnergyKJ,
        body.FatEnergyKJ, body.GutEnergyKJ, Math.Clamp(body.GlycogenEnergyKJ / profile.GlycogenCapacityKJ, 0, 1));
}

/// <summary>Derived effects, never a second mutable reserve or saved buff stack.</summary>
public readonly record struct PlayerCondition(double NutritionRecoverySupport, double RestedSupport,
    double RecoveryMultiplier, double StaminaRecoveryMultiplier, double FatigueRecoveryMultiplier)
{
    public static PlayerCondition Evaluate(FrameworkState state, BodyState body, BodyProfile profile)
    {
        // Food quality supports the immune system through five explicit reserves.
        // It does not silently multiply all healing, stamina and rest rates.
        var recovery = Math.Min(Ketosis.Performance(state.Clinical.Ketosis), Exertion.FatiguePerformance(state.Fatigue));
        return new(0, 0, NutrientInteractions.RecoveryMultiplier(state.Nutrition), recovery, 1);
    }
}
