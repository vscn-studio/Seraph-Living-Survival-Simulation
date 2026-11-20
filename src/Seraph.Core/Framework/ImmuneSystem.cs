using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>One effective efficiency for display, susceptibility and disease progression. Memory is pathogen-specific.</summary>
public static class ImmuneSystem
{
    public static double Effective(FrameworkState state) => Math.Clamp((state.ImmuneReserve * (1 + MicroNutrition.ImmuneBonus(state.Nutrition)))
        * (1 - .4 * BodyFunctionResolver.StructuralDamage(state, "torso"))
        * (1 - .3 * Math.Max(state.Blood.Toxicity, Math.Max(state.ToxicBurden, state.DrugToxicBurden))), .1, 1.5);

    public static double Support(FrameworkState state, BodyState body, BodyProfile profile)
    {
        const double nutrients = 1; // Micronutrient bonuses are applied once by Effective.
        var energy = .75 + .25 * Math.Clamp((body.GlycogenEnergyKJ + body.FatEnergyKJ)
            / Math.Max(1, profile.GlycogenCapacityKJ * .5), 0, 1);
        var rest = 1 - .25 * Math.Max(state.Fatigue, body.SleepPressure);
        var temperature = Math.Clamp(1 - Math.Max(35.5 - body.CoreTemperatureC, body.CoreTemperatureC - 38.5) * .15, .5, 1);
        return Math.Clamp(nutrients * energy * rest * temperature, 0, 1);
    }
    public static double Susceptibility(FrameworkState state) => 1 + (1 - Effective(state)) * 1.5;
    public static double Duration(FrameworkState state, DiseaseDefinition definition, DiseasePhase phase)
    {
        var deficit = 1 - Effective(state);
        return phase switch {
            DiseasePhase.Incubating => definition.IncubationSeconds * (1 - deficit * .25),
            DiseasePhase.Symptomatic => definition.SymptomaticSeconds * (1 + deficit * .75),
            _ => definition.RecoverySeconds * (1 + deficit * .5)
        };
    }
}
