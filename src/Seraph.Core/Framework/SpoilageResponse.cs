using Seraph.Core.State;

namespace Seraph.Core.Framework;

public readonly record struct VomitResult(FrameworkState State, BodyState Body, bool Triggered);

/// <summary>Deterministic consequences of eating spoiled food.</summary>
public static class SpoilageResponse
{
    /// <summary>One-time dirtiness increase for handling or eating spoiled food.</summary>
    public static double DirtinessGain(double spoilage, bool eaten)
    {
        if (!double.IsFinite(spoilage) || spoilage <= .1) return 0;
        return spoilage switch
        {
            <= .3 => eaten ? .03 : .02,
            <= .6 => eaten ? .08 : .05,
            _ => eaten ? .15 : .10
        };
    }

    public static double VomitProbability(double spoilage)
    {
        if (!double.IsFinite(spoilage) || spoilage < .3) return 0;
        return Math.Clamp(.15 + (spoilage - .3) / .7 * .6, .15, .75);
    }

    /// <summary>Removes 60% of the still-unabsorbed stomach queue; absorbed stores are untouched.</summary>
    public static VomitResult TryVomit(FrameworkState state, BodyState body, BodyProfile profile, double spoilage)
    {
        var random = new DeterministicRandom(state.RandomState);
        var draw = random.NextUnit(); // One saved-stream draw per accepted spoiled-food intake.
        state = state with { RandomState = random.State };
        if (draw >= VomitProbability(spoilage)) return new(state, body, false);

        const double expelled = .6;
        var diet = state.Nutrition;
        var removedMass = diet.GutMassG * expelled;
        var removedWater = diet.GutFoodWaterG * expelled;
        var removedWaste = diet.StomachWasteG * expelled;
        var pending = diet.Pending.Select(v => v * (1 - expelled)).ToArray();
        var remainingWater = body.GutWaterL * (1 - expelled);
        var remainingEnergy = body.GutEnergyKJ * (1 - expelled);
        state = state with {
            ImmuneReserve = Math.Clamp(state.ImmuneReserve - .2, 0, 1),
            Nutrition = diet with {
                Pending = pending,
                GutMassG = Math.Max(0, diet.GutMassG - removedMass),
                GutFoodWaterG = Math.Max(0, diet.GutFoodWaterG - removedWater),
                StomachWasteG = Math.Max(0, diet.StomachWasteG - removedWaste),
                PendingWaterWasteMl = diet.PendingWaterWasteMl * (1 - expelled)
            },
            Syndromes = state.Syndromes with {
                // A brief diarrheal episode creates urgency and a small real bowel load.
                BowelContentsG = Math.Min(1e12, state.Syndromes.BowelContentsG + 250)
            }
        };
        body = body with { GutWaterL = remainingWater, GutEnergyKJ = remainingEnergy };
        if (diet.Validate() is not null || state.Validate() is not null || body.Validate(profile) is not null)
            throw new InvalidOperationException("Vomiting produced an invalid physiology state.");
        return new(state, body, true);
    }
}
