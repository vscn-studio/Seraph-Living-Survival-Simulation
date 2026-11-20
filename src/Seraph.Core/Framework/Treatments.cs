using Seraph.Core.State;

namespace Seraph.Core.Framework;

public enum TreatmentRoute { Oral, Topical }

/// <summary>Registered gameplay dose. No real-world medical dosage is represented.</summary>
public sealed record TreatmentDefinition
{
    public int TimeUnitVersion { get; init; } = 1;
    // DelaySeconds and DurationSeconds are normalized game seconds. No implicit conversion.
    public string Id { get; init; } = "";
    public string Group { get; init; } = "";
    public TreatmentRoute Route { get; init; }
    public string[] Pathogens { get; init; } = [];
    public string[] Symptoms { get; init; } = [];
    public double DelaySeconds { get; init; } = 60;
    public double DurationSeconds { get; init; } = 900;
    public double Relief { get; init; } = .4;
    public double RecoveryBonus { get; init; } = 1;
    public double SleepSupport { get; init; }
    public double WoundRepair { get; init; }
    public double CleanFraction { get; init; }
    public double Hemostasis { get; init; }
    public double WaterL { get; init; }
    public double AlcoholL { get; init; }
    public double Toxicity { get; init; }
    public bool RequiresWound { get; init; }
    public WoundKind[] WoundKinds { get; init; } = [];
    public string? Validate() => TimeUnitVersion != 1 || !Key(Id) || !Key(Group) || !Enum.IsDefined(Route)
        || Pathogens is null || Symptoms is null || WoundKinds is null
        || Pathogens.Length > 16 || Symptoms.Length > 16 || WoundKinds.Length > 8
        || Pathogens.Any(p => !Key(p)) || Symptoms.Any(p => !Key(p)) || WoundKinds.Any(p => !Enum.IsDefined(p))
        || !BodyProfile.In(DelaySeconds, 0, 108000) || !BodyProfile.In(DurationSeconds, 1, 2592000)
        || !BodyProfile.In(Relief, 0, .6) || !BodyProfile.In(RecoveryBonus, 0, 1.5)
        || !BodyProfile.In(SleepSupport, 0, .3) || !BodyProfile.In(WoundRepair, 0, 1)
        || !BodyProfile.In(CleanFraction, 0, 1) || !BodyProfile.In(Hemostasis, 0, 1)
        || !BodyProfile.In(WaterL, 0, .25) || !BodyProfile.In(AlcoholL, 0, .05)
        || !BodyProfile.In(Toxicity, 0, 1) || (WoundRepair > 0 || CleanFraction > 0 || Hemostasis > 0) && !RequiresWound
        || RequiresWound && Route != TreatmentRoute.Topical || Route == TreatmentRoute.Topical && (WaterL > 0 || AlcoholL > 0)
        ? "Invalid treatment definition." : null;
    internal static bool Key(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 96;
}

public sealed record ActiveTreatment(TreatmentDefinition Definition, string Source, long Wound,
    double StartsBioSeconds, double EndsBioSeconds);
public sealed record TreatmentDose(string Group, double BioSeconds);
public sealed record Complaint(string Id, string Source, double StartsBioSeconds, double EndsBioSeconds, double Severity);

public static class Treatments
{
    private static double Strength(TreatmentDefinition d) => d.Relief + d.RecoveryBonus + d.WoundRepair + d.CleanFraction + d.Hemostasis + d.SleepSupport;
    public static IEnumerable<ActiveTreatment> Active(FrameworkState s, double? at = null) => s.Treatments.Where(t => s.TreatmentEffectsEnabled &&
        t.StartsBioSeconds <= (at ?? s.TreatmentClockBioSeconds) && t.EndsBioSeconds > (at ?? s.TreatmentClockBioSeconds));
    public static double Relief(FrameworkState s, string symptom, long wound = 0) => Math.Min(.6,
        1 - Active(s).Where(t => t.Definition.Symptoms.Contains(symptom) && (t.Wound == 0 || t.Wound == wound))
            .Aggregate(1d, (r, t) => r * (1 - t.Definition.Relief)));
    public static double Speed(FrameworkState s, string pathogen, double? at = null) => Math.Min(2.5,
        1 + Active(s, at).Where(t => t.Definition.Pathogens.Contains(pathogen)).Sum(t => t.Definition.RecoveryBonus));
    public static double Burden(FrameworkState s, DiseaseState d) => d.Burden * (1 - Relief(s, d.Pathogen));
    public static double SleepQuality(FrameworkState s, double observed) => Math.Clamp(observed
        + Active(s).Select(t => t.Definition.SleepSupport).DefaultIfEmpty().Max(), 0, 1);
    public static double Pain(FrameworkState s, double lactateCapacity = 100) => Math.Max(WoundPain.Resolve(s.Wounds, w => Relief(s, "pain", w.EventSequence)),
        .35 * s.MuscleSoreness) * AdrenalineEffects.Pain(s);
    public static double ComplaintSeverity(FrameworkState s, string id) => s.Complaints.Where(c => c.Id == id)
        .Select(c => c.Severity * Math.Clamp((c.EndsBioSeconds - s.TreatmentClockBioSeconds)
            / (c.EndsBioSeconds - c.StartsBioSeconds), 0, 1)).DefaultIfEmpty().Max() * (1 - Relief(s, id));
    public static int RecentDoses(FrameworkState s, string group, double now) => s.TreatmentDoses.Count(d =>
        d.Group == group && d.BioSeconds <= now && now - d.BioSeconds < 18000);
    public static bool MatchesWound(TreatmentDefinition d, Wound w) => w.Healing < 1
        && (d.WoundKinds.Length == 0 || d.WoundKinds.Contains(w.Kind));

    public static FrameworkState Complain(FrameworkState s, string id, string source, double now, double duration, double severity)
        => s with { Complaints = s.Complaints.Where(c => c.Id != id && c.EndsBioSeconds > now).TakeLast(15)
            .Append(new(id, source, now, now + duration, severity)).ToArray() };

    public static FrameworkResult Apply(FrameworkState old, TreatmentDefinition def, string receipt, string source,
        TreatmentRoute route, double now, long wound = 0)
    {
        var error = old.Validate() ?? def.Validate();
        if (error is not null) return new(false, old, error);
        if (!TreatmentDefinition.Key(receipt) || !TreatmentDefinition.Key(source) || route != def.Route
            || !BodyProfile.In(now, old.TreatmentClockBioSeconds, 1e15)
            || !old.TreatmentEffectsEnabled || old.RecentReceipts.Contains(receipt) || def.RequiresWound && !old.Wounds.Any(w => w.EventSequence == wound && MatchesWound(def, w))
            || !def.RequiresWound && wound != 0) return new(false, old, "Invalid route, wound or repeated dose receipt.");
        var repeats = RecentDoses(old, def.Group, now);
        var previous = old.Treatments.FirstOrDefault(t => t.Definition.Group == def.Group && t.Wound == wound && t.EndsBioSeconds > now);
        // Preserve the strongest active preparation, but never extend by summing durations.
        var strongest = previous is not null && Strength(previous.Definition) > Strength(def) ? previous.Definition : def;
        var list = old.Treatments.Where(t => t.EndsBioSeconds > now && !(t.Definition.Group == def.Group && t.Wound == wound)).ToList();
        if (list.Count >= 32) return new(false, old, "Treatment capacity reached.");
        list.Add(new(strongest, source, wound, previous is not null && previous.StartsBioSeconds <= now ? now : now + def.DelaySeconds,
            now + (previous is not null && previous.StartsBioSeconds <= now ? 0 : def.DelaySeconds) + def.DurationSeconds));
        var next = old with {
            EventSequence = checked(old.EventSequence + 1), TreatmentClockBioSeconds = now,
            Treatments = list.ToArray(), RecentReceipts = old.RecentReceipts.TakeLast(255).Append(receipt).ToArray(),
            TreatmentDoses = old.TreatmentDoses.Where(d => now - d.BioSeconds < 18000).TakeLast(63).Append(new(def.Group, now)).ToArray(),
            DrugToxicBurden = Math.Clamp(old.DrugToxicBurden + def.Toxicity + (repeats >= 2 ? .2 : 0), 0, 1),
            AlcoholBurden = Math.Clamp(old.AlcoholBurden + def.AlcoholL * 20, 0, 1)
        };
        if (repeats >= 1) next = Complain(next, "nausea", source, now, 5400, .35);
        if (def.SleepSupport > 0) next = Complain(next, "sedated", source, now, def.DurationSeconds, .12);
        return next.Validate() is { } invalid ? new(false, old, invalid) : new(true, next, null);
    }

    public static string? Validate(FrameworkState s)
    {
        if (!BodyProfile.In(s.TreatmentClockBioSeconds, 0, 1e15) || !BodyProfile.In(s.DrugToxicBurden, 0, 1)
            || !BodyProfile.In(s.AlcoholBurden, 0, 1) || s.Treatments is null || s.TreatmentDoses is null || s.Complaints is null
            || s.Treatments.Length > 32 || s.TreatmentDoses.Length > 64 || s.Complaints.Length > 16) return "Invalid treatment state.";
        foreach (var t in s.Treatments)
            if (t is null || t.Definition is null || t.Definition.Validate() is not null || !TreatmentDefinition.Key(t.Source)
                || !BodyProfile.In(t.StartsBioSeconds, 0, 1e15) || !BodyProfile.In(t.EndsBioSeconds, t.StartsBioSeconds + 1, 1e15)
                || t.Wound < 0 || t.Wound > s.EventSequence) return "Invalid saved treatment.";
        if (s.Treatments.Select(t => (t.Definition.Group, t.Wound)).Distinct().Count() != s.Treatments.Length) return "Duplicate effect group.";
        if (s.TreatmentDoses.Any(d => d is null || !TreatmentDefinition.Key(d.Group) || !BodyProfile.In(d.BioSeconds, 0, 1e15))
            || s.Complaints.Any(c => c is null || !TreatmentDefinition.Key(c.Id) || !TreatmentDefinition.Key(c.Source)
                || !BodyProfile.In(c.StartsBioSeconds, 0, 1e15) || !BodyProfile.In(c.EndsBioSeconds, c.StartsBioSeconds + 1, 1e15)
                || !BodyProfile.In(c.Severity, 0, 1))) return "Invalid saved dose or complaint.";
        return null;
    }
}
