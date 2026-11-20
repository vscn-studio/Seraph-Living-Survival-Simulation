using Seraph.Core.State;

namespace Seraph.Core.Framework;

public enum WaterTreatmentStage { Raw = 0, Filtered = 3, Disinfected = 4 }

/// <summary>Conserved water batch. Hazard is a game preset, never CFU or a medical measurement.</summary>
public sealed record WaterBatch
{
    // Individual reservoirs enforce their own capacities below this batch limit.
    public const double MaximumVolumeL = 1_024_000;

    [System.Text.Json.Serialization.JsonRequired]
    public string Format { get; init; } = "seraph.release.water";
    [System.Text.Json.Serialization.JsonRequired]
    public int QualityVersion { get; init; } = WaterQualityCompatibility.CurrentVersion;
    public double VolumeL { get; init; }
    public double TemperatureC { get; init; } = 15;
    public double SaltGramsPerL { get; init; }
    /// <summary>Unidentified enteric contamination, including legacy scalar saves.</summary>
    public double EntericHazardPerL { get; init; }
    public IReadOnlyDictionary<string, double> PathogensPerL { get; init; } = PathogenLoads.Empty;
    public double TotalEntericHazardPerL => EntericHazardPerL + PathogensPerL.Where(p => Pathogens.Enteric(p.Key)).Sum(p => p.Value);
    public double ChemicalHazardPerL { get; init; }
    public double Turbidity { get; init; }
    // Optional for existing saves; new batches carry the natural solids through mixing/treatment.
    public double? BaselineTurbidity { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double NaturalTurbidity => Math.Min(Turbidity, BaselineTurbidity
        ?? (SurfaceWater.BaseTurbidity(OriginSource ?? Source) is > 0 and var baseline ? baseline : Turbidity));
    [System.Text.Json.Serialization.JsonIgnore]
    public double AddedTurbidity => Math.Max(0, Turbidity - NaturalTurbidity);
    // Mud is suspended particulate matter, not a dissolved blood toxin. Kept for API compatibility.
    public double PotentialBloodWasteMl(double volumeL) => 0;
    public bool Unverified { get; init; } = true;
    public string Source { get; init; } = "unverified";
    public string? OriginSource { get; init; }
    public WaterTreatmentStage TreatmentStage { get; init; } = WaterTreatmentStage.Raw;

    public string? Validate() => !BodyProfile.In(VolumeL, 0, MaximumVolumeL)
        || !BodyProfile.In(TemperatureC, -80, 110) || !BodyProfile.In(SaltGramsPerL, 0, 400)
        || !BodyProfile.In(EntericHazardPerL, 0, 100) || !BodyProfile.In(ChemicalHazardPerL, 0, 1) || !BodyProfile.In(Turbidity, 0, 1)
        || BaselineTurbidity is { } baseline && !BodyProfile.In(baseline, 0, 1)
        || !PathogenLoads.Valid(PathogensPerL) || !Enum.IsDefined(TreatmentStage)
        || Format != "seraph.release.water" || QualityVersion != WaterQualityCompatibility.CurrentVersion
        || OriginSource is { Length: > 64 } || Source is null || Source.Length is 0 or > 64 ? "Invalid water batch." : null;

    public WaterBatch Mix(WaterBatch other)
    {
        if (Validate() is not null || other.Validate() is not null || VolumeL + other.VolumeL > MaximumVolumeL)
            throw new ArgumentException("Invalid water mixture.");
        if (VolumeL == 0) return other;
        if (other.VolumeL == 0) return this;
        var total = VolumeL + other.VolumeL;
        double Weighted(double a, double b) => (a * VolumeL + b * other.VolumeL) / total;
        return new() { VolumeL = total, TemperatureC = Weighted(TemperatureC, other.TemperatureC),
            PathogensPerL = PathogenLoads.Mix(PathogensPerL, VolumeL, other.PathogensPerL, other.VolumeL),
            SaltGramsPerL = Weighted(SaltGramsPerL, other.SaltGramsPerL),
            EntericHazardPerL = Weighted(EntericHazardPerL, other.EntericHazardPerL),
            ChemicalHazardPerL = 0,
            Turbidity = Weighted(Turbidity, other.Turbidity), BaselineTurbidity = Weighted(NaturalTurbidity, other.NaturalTurbidity),
            Unverified = Unverified || other.Unverified,
            TreatmentStage = (WaterTreatmentStage)Math.Min((int)TreatmentStage, (int)other.TreatmentStage),
            OriginSource = OriginSource == other.OriginSource ? OriginSource : "mixed",
            Source = Source == other.Source ? Source : "mixed" };
    }
}

/// <summary>Release v1 bowel and deferred transit stores, in grams.</summary>
public sealed record InteractionState
{
    public int Version { get; init; } = 1;
    public double BowelUnits { get; init; }
    public double PendingBowelUnits { get; init; }
    public long DefecationCount { get; init; }
    public WaterBatch? LastWater { get; init; }
    public double AfterDefecation(double completedUnits)
    {
        if (!double.IsFinite(completedUnits) || completedUnits <= 0 || completedUnits > BowelUnits)
            throw new ArgumentOutOfRangeException(nameof(completedUnits));
        return BowelUnits - completedUnits;
    }
    public string? Validate() => Version != 1 || !BodyProfile.In(BowelUnits, 0, BowelSettlement.CapacityG)
        || !BodyProfile.In(PendingBowelUnits, 0, BowelSettlement.MaximumPendingG)
        || DefecationCount < 0 ? "Invalid interaction state." : LastWater?.Validate();
}
