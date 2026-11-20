using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>One mass ledger shared by live integration and sleep preview. All quantities are grams.</summary>
public static class BowelSettlement
{
    public const double CapacityG = 5000;
    public const double ManualMinimumG = 500;
    public const double PortionG = 1000;
    public const double WarningG = 4000;
    // A deferred transit ledger, not extra physical bowel capacity. Never truncate accepted food residue.
    public const double MaximumPendingG = 1e12;
    public static InteractionState Advance(InteractionState before, double dietaryResidueG, double clinicalG, double seconds)
    {
        if (before.Validate() is not null || !BodyProfile.In(seconds, 0, 8)
            || !BodyProfile.In(dietaryResidueG, 0, 5000)
            || !BodyProfile.In(clinicalG, 0, CapacityG - before.BowelUnits))
            throw new ArgumentOutOfRangeException(nameof(dietaryResidueG));
        var pending = before.PendingBowelUnits + dietaryResidueG;
        if (pending > MaximumPendingG) throw new InvalidOperationException("Bowel transit ledger is full.");
        var bowel = before.BowelUnits + clinicalG;
        var transfer = Math.Min(pending, Math.Min(seconds * .1, Math.Max(0, CapacityG - bowel)));
        return before with { Version = 1, BowelUnits = bowel + transfer, PendingBowelUnits = pending - transfer };
    }
    public static DefecationPlan PlanDefecation(double bowelG, int freeItems)
    {
        if (!BodyProfile.In(bowelG, 0, CapacityG) || freeItems < 0) throw new ArgumentOutOfRangeException(nameof(bowelG));
        if (bowelG < ManualMinimumG) return new(0, 0, 0, 0);
        var fullItems = (int)(bowelG / PortionG);
        var items = Math.Min(fullItems, freeItems);
        var product = items * PortionG;
        var removed = items == fullItems ? bowelG : product;
        return new(items, removed, product, removed - product);
    }
}

/// <summary>Tail disposal is intentional gameplay loss, never extra fertilizer or pollution mass.</summary>
public readonly record struct DefecationPlan(int Items, double MassG, double ProductMassG, double DiscardedG)
{
    public void Deconstruct(out int items, out double massG) { items = Items; massG = MassG; }
}
