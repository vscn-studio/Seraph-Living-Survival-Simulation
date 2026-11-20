using Seraph.Core.State;

namespace Seraph.Core.Framework;

public readonly record struct WoundRecoveryResult(FrameworkState State, BodyState Body, double EnergyKJ, double ProteinG)
{
    public double BowelG { get; init; }
}

/// <summary>Commits proposed closure against actual absorbed protein and fuel, also during sleep preview.</summary>
public static class WoundRecovery
{
    public const double ProteinPerHp = 2;
    public const double EnergyKJPerHp = 10 * 4.184;
    public static double DamagePoints(Wound w) => w.IntegrityDamage
        ?? w.Severity * VitalState.MaximumFor(Anatomy.VitalIndex(w.Region));

    public static WoundRecoveryResult Settle(FrameworkState before, FrameworkState proposed, BodyState body,
        BodyProfile profile, double seconds, bool allowUntrackedRecovery, double healingMultiplier = 1)
    {
        if (seconds <= 0) return new(proposed, body, 0, 0);
        var availableProtein = DietaryMetabolism.AvailableProtein(proposed.Nutrition);
        var budget = proposed.Vitals.Lethal && !proposed.Downed.Active ? 0 : Math.Min(availableProtein / ProteinPerHp, (body.GlycogenEnergyKJ + body.FatEnergyKJ) / EnergyKJPerHp);
        var paid = 0d;
        var vitals = proposed.Vitals;
        var next = proposed.Wounds.ToDictionary(w => w.EventSequence);
        // A missing proposed wound has just reached full closure. Do not discard it before payment.
        foreach (var old in before.Wounds.OrderByDescending(BodyFunctionResolver.Remaining))
        {
            var wound = next.GetValueOrDefault(old.EventSequence) ?? old with { Healing = 1 };
            var points = DamagePoints(old);
            var region = Anatomy.VitalIndex(old.Region);
            var scale = 1d; // IntegrityDamage is an absolute HP deficit, independent of current maximum.
            var desired = Math.Max(0, wound.Healing - old.Healing) * points * scale;
            var accepted = Math.Min(desired, Math.Max(0, budget - paid));
            var healing = points > 0 ? old.Healing + accepted / (points * scale) : wound.Healing;
            next[old.EventSequence] = WoundPain.PaidHealing(old, wound, Math.Clamp(healing, 0, 1));
            vitals = vitals.Restore(accepted, Anatomy.VitalIndex(old.Region));
            paid += accepted;
        }
        // Native/direct health changes without a wound still need the same material payment.
        if (before.Wounds.Length == 0 && allowUntrackedRecovery)
        {
            var accepted = Math.Min(Math.Max(0, budget - paid), Math.Min(vitals.EffectiveMaximum - vitals.Health,
                seconds * 2 / 3600 * PlayerCondition.Evaluate(proposed, body, profile).RecoveryMultiplier
                    * (BodyProfile.In(healingMultiplier, 0, 100) ? healingMultiplier : 1)));
            vitals = vitals.Change(accepted);
            paid += accepted;
        }
        var protein = paid * ProteinPerHp;
        var energy = paid * EnergyKJPerHp;
        var glycogen = Math.Min(body.GlycogenEnergyKJ, energy);
        var wounds = next.Values.Where(w => w.Healing < 1 - 1e-12).OrderBy(w => w.EventSequence).ToArray();
        var state = proposed with { Vitals = vitals, Wounds = wounds,
            Nutrition = DietaryMetabolism.SpendProtein(proposed.Nutrition, protein) };
        return new(state with { Pain = Treatments.Pain(state, BloodCirculation.LactateCapacityG(body, profile)) }, body with {
            GlycogenEnergyKJ = Math.Max(0, body.GlycogenEnergyKJ - glycogen),
            FatEnergyKJ = Math.Max(0, body.FatEnergyKJ - (energy - glycogen)) }, energy, protein);
    }
}
