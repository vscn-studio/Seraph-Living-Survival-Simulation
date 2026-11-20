using Seraph.Core.State;
using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Seraph.Core.Framework;

public sealed record NutrientDefinition(string Id, string Unit, double DailyBudget, string Color)
{
    // A bounded game reserve, not a clinical concentration or toxicity threshold.
    // Preserve the saved 0..100 reserve scale while tuning daily micronutrient use.
    public double Maximum => IsMicro ? 100 : DailyBudget * 3;
    public bool UsesCalendar => Id is not ("carbohydrate" or "protein" or "fat" or "fiber");
    // Micronutrient reserve drain is calibrated independently from its display capacity.
    public double DepletionPerDay => IsMicro ? MicroNutrition.DailyPointsFor(Id) : IsElectrolyte ? 3.5 : DailyBudget * (UsesCalendar ? .15 : 1);
    public bool IsMicro => Id is "iron" or "zinc" or "a" or "c" or "calcium" or "bcomplex";
    public bool IsElectrolyte => Id == "electrolytes";
}

public static class Nutrients
{
    public static readonly NutrientDefinition[] All = [
        new("carbohydrate", "g", 275, "#e4b744"), new("protein", "g", 75, "#dc715e"),
        new("fat", "g", 70, "#e5d06a"), new("fiber", "g", 30, "#80ac56"),
        new("electrolytes", "g", 3.5, "#72b8dc"),
        new("calcium", "%", 22, "#c5d8e5"),
        // D and K remain reserved array slots for existing saves; they do not support gameplay.
        new("a", "%", 22, "#e59a54"), new("d", "µg", 15, "#e7c65c"),
        new("k", "µg", 120, "#78bd70"), new("bcomplex", "%", 22, "#c897d3"),
        new("iron", "%", 22, "#be8774"),
        new("zinc", "%", 22, "#a5aaa8"), new("c", "%", 22, "#e4bf4c")
    ];

    private static readonly FrozenDictionary<string, int> Indices = All.Select((n, i) => (n.Id, Index: i))
        .ToFrozenDictionary(n => n.Id, n => n.Index, StringComparer.Ordinal);
    public static int IndexOf(string id)
    {
        if (id == "ironzinc") id = "iron";
        if (Indices.TryGetValue(id, out var index)) return index;
        // Read-only names for third-party callers; persisted arrays require release v1.
        var alias = id switch { "efa" => "fat", "sodium" or "potassium" or "chloride" => "electrolytes", _ => "" };
        return alias.Length > 0 && Indices.TryGetValue(alias, out index) ? index : -1;
    }
}

/// <summary>A read-only view; amounts in different units are never added together.</summary>
public readonly record struct NutrientStatus(bool Known, double Reserve, double Pending, double DailyBudget, double Deficiency)
{
    public double DaysAvailable => Known && DailyBudget > 0 ? Reserve / DailyBudget : 0;
    public double Score => Math.Clamp(Reserve, 0, 100);
    public NutrientStage Stage => NutritionBands.Stage(Score);
    public double Sufficiency => Known ? NutritionBands.Support(Stage) : 0;
}

public enum NutrientStage { Empty, VeryLow, Low, Normal, Abundant }

/// <summary>Stable gameplay bands; internal mass and slow deficiency history remain conserved.</summary>
public static class NutritionBands
{
    public static NutrientStage Stage(double score) => score switch {
        <= .01 => NutrientStage.Empty, <= 80 => NutrientStage.Normal, _ => NutrientStage.Abundant
    };
    public static double Support(NutrientStage stage) => stage switch {
        NutrientStage.Empty => 0, NutrientStage.VeryLow => .25, NutrientStage.Low => .5, _ => 1
    };
    public static double DeficiencyEffect(double burden) => Math.Clamp(burden, 0, 1);
}

public readonly record struct NutritionAssessment(double Coverage, double Sufficiency);

public sealed record NutritionState
{
    [JsonRequired] public int Version { get; init; } = 1;
    // Absent in older saves: initialize the formerly unused zinc slot once.
    public bool IndependentZinc { get; init; }
    public int CalibrationVersion { get; init; } = 1;
    public double StomachWasteG { get; init; }
    public double BowelProducedG { get; init; }
    public double RepairEnergyKJ { get; init; }
    /// <summary>Cumulative filtered score points; the circulation ledger consumes only the step delta.</summary>
    public double FilteredMicroPoints { get; init; }
    public double PendingWaterWasteMl { get; init; }
    public int WaterWasteSchema { get; init; }
    public double AbsorbedWaterWasteMl { get; init; }
    [JsonRequired] public double[] Reserve { get; init; } = new double[Nutrients.All.Length];
    [JsonRequired] public double[] Pending { get; init; } = new double[Nutrients.All.Length];
    [JsonRequired] public bool[] Known { get; init; } = new bool[Nutrients.All.Length];
    /// <summary>Slow bounded burden from sustained low reserve; this is not a diagnosis.</summary>
    [JsonRequired] public double[] DeficiencyBurden { get; init; } = new double[Nutrients.All.Length];
    public double GutMassG { get; init; }
    /// <summary>Water already included in the food-mass queue, decaying with that queue.
    /// This is a mass reference only; actual unabsorbed water lives in BodyState.GutWaterL.</summary>
    public double GutFoodWaterG { get; init; }
    // Fiber occupies real stomach space; it passes to the bowel rather than becoming a body reserve.
    public double StomachFiberG => Math.Min(Pending[Nutrients.IndexOf("fiber")], Math.Max(0, GutMassG - GutFoodWaterG));
    public double ContentsMassG(double gutWaterL) => Math.Max(0, GutMassG - GutFoodWaterG) + gutWaterL * 1000;
    public double BowelResidueG(NutritionState after) => Math.Max(0, after.BowelProducedG - BowelProducedG);
    public double TotalFoodG { get; init; }
    public string LastFood { get; init; } = "";
    public long UnmappedPortions { get; init; }

    public static NutritionState Starting() => new() {
        CalibrationVersion = 2,
        IndependentZinc = true,
        Reserve = Nutrients.All.Select(n => n.Id is "carbohydrate" or "protein" or "fat" or "fiber" ? 0 : n.Maximum * .5).ToArray(),
        Known = Enumerable.Repeat(true, Nutrients.All.Length).ToArray()
    };

    public NutrientStatus Get(string id)
    {
        var i = Nutrients.IndexOf(id);
        return i >= 0 && i < Reserve.Length && i < Pending.Length && i < Known.Length && i < DeficiencyBurden.Length
            ? new(Known[i], Reserve[i], Pending[i], Nutrients.All[i].DepletionPerDay, Known[i] ? DeficiencyBurden[i] : 0)
            : new(false, 0, 0, 0, 0);
    }

    public NutritionAssessment Assess()
    {
        var known = 0;
        var sufficient = 0.0;
        foreach (var definition in Nutrients.All.Where(n => n.IsMicro))
        {
            var status = Get(definition.Id);
            if (!status.Known) continue;
            known++;
            sufficient += status.Sufficiency;
        }
        return new(known / (double)MicroNutrition.Groups.Length, known == 0 ? 0 : sufficient / known);
    }

    public string? Validate() => Version != 1 || CalibrationVersion is < 1 or > 2 || !BodyProfile.In(StomachWasteG, 0, 5000)
        || !BodyProfile.In(BowelProducedG, 0, 1e12)
        || !BodyProfile.In(RepairEnergyKJ, 0, 1e9)
        || !BodyProfile.In(PendingWaterWasteMl, 0, 1e9) || !BodyProfile.In(AbsorbedWaterWasteMl, 0, 1e12)
        || !BodyProfile.In(FilteredMicroPoints, 0, 1e12) || Reserve is null || Pending is null || Known is null || DeficiencyBurden is null
        || Reserve.Length != Nutrients.All.Length || Pending.Length != Reserve.Length || Known.Length != Reserve.Length
        || DeficiencyBurden.Length != Reserve.Length
        || Reserve.Where((v, i) => !BodyProfile.In(v, 0, i == Nutrients.IndexOf("electrolytes") && CalibrationVersion == 1 ? 100 : Nutrients.All[i].Maximum)).Any()
        || Pending.Any(v => !BodyProfile.In(v, 0, 1e9)) || DeficiencyBurden.Any(v => !BodyProfile.In(v, 0, 1))
        || !BodyProfile.In(GutMassG, 0, 5000)
        || !BodyProfile.In(GutFoodWaterG, 0, GutMassG)
        || !BodyProfile.In(TotalFoodG, 0, 1e12) || UnmappedPortions < 0 || LastFood is null || LastFood.Length > 128
        ? "Invalid independent nutrition state." : null;

    public NutritionState Advance(double dt, double absorbedEnergy, double previousGutEnergy, double? calendarDays = null,
        double bowelTransitRoomG = 5000)
    {
        if (Validate() is { } error) throw new InvalidOperationException(error);
        // Summed physical substeps may exceed their starting queue by a few rounding bits.
        // The fraction below still clamps to one, so this tolerance never adds nutrients.
        if (!BodyProfile.In(dt, 0, 8) || !BodyProfile.In(previousGutEnergy, 0, 1e9)
            || !BodyProfile.In(absorbedEnergy, 0, previousGutEnergy + 1e-9) || dt == 0 && absorbedEnergy != 0)
            throw new ArgumentOutOfRangeException(nameof(dt), "Invalid nutrition time or absorption flux.");
        if (calendarDays is { } days && !BodyProfile.In(days, 0, 1)) throw new ArgumentOutOfRangeException(nameof(calendarDays));
        if (!BodyProfile.In(bowelTransitRoomG, 0, BowelSettlement.MaximumPendingG)) throw new ArgumentOutOfRangeException(nameof(bowelTransitRoomG));
        if (dt == 0) return this;
        var profile = BodyProfile.Default;
        var water = GutFoodWaterG / 1000;
        var body = BodyState.Create(profile) with { GutWaterL = water, GutEnergyKJ = previousGutEnergy,
            BodyWaterL = Math.Max(0, profile.ReferenceBodyWaterL - water) };
        return DietaryMetabolism.Digest(this, body, profile, dt, 1, bowelTransitRoomG).Nutrition;
    }
}
