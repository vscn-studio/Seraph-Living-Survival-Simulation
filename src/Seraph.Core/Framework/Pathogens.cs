namespace Seraph.Core.Framework;

public enum ExposureRoute { Ingestion, OpenWound, Airborne, Skin, Internal, Vector }

/// <summary>Named agents and routes. Durations are game balance, not measured microbiology.</summary>
public sealed record PathogenProfile(string Id, string Agent, ExposureRoute Route, DiseaseDefinition Disease);

public static class Pathogens
{
    public static readonly IReadOnlyList<PathogenProfile> All = Array.AsReadOnly<PathogenProfile>([
        new("norovirus", "Norovirus", ExposureRoute.Ingestion, new("norovirus", 300, 1800, 1500, .6)),
        new("giardia", "Giardia duodenalis", ExposureRoute.Ingestion, new("giardia", 600, 3600, 2700, .5)),
        new("shigella", "Shigella", ExposureRoute.Ingestion, new("shigella", 600, 2700, 2100, .65)),
        new("ecoli", "Pathogenic Escherichia coli", ExposureRoute.Ingestion, new("ecoli", 300, 1800, 1500, .55)),
        new("salmonella", "Salmonella", ExposureRoute.Ingestion, new("salmonella", 600, 2700, 2100, .6)),
        new("wound-staph", "Staphylococcus aureus", ExposureRoute.OpenWound, new("wound-staph", 600, 3600, 2700, .5)),
        new("respiratory", "Environmental respiratory pathogen", ExposureRoute.Airborne,
            new("respiratory", 600, 2400, 1800, .45)),
        new("common-cold", "Common cold virus", ExposureRoute.Airborne,
            new("common-cold", 900, 3600, 2400, .5)),
        new("qfever", "Coxiella burnetii", ExposureRoute.Airborne, new("qfever", 720, 2880, 2880, .65)),
        new("tularemia", "Francisella tularensis", ExposureRoute.Airborne, new("tularemia", 720, 2880, 2880, .7)),
        new("histoplasmosis", "Histoplasma", ExposureRoute.Airborne, new("histoplasmosis", 1440, 2880, 2880, .55)),
        new("pasteurella", "Pasteurella multocida", ExposureRoute.Internal, new("pasteurella", 360, 2880, 2160, .65)),
        new("sporotrichosis", "Sporothrix", ExposureRoute.Internal, new("sporotrichosis", 1440, 4320, 2880, .5)),
        new("murinetyphus", "Rickettsia typhi", ExposureRoute.Vector, new("murinetyphus", 720, 2880, 2160, .65)),
        new("malaria", "Plasmodium", ExposureRoute.Vector, new("malaria", 1440, 4320, 2880, .7)),
        new("trichinosis", "Trichinella", ExposureRoute.Ingestion, new("trichinosis", 720, 2880, 2880, .6)),
        new("anthrax", "Bacillus anthracis", ExposureRoute.Skin, new("anthrax", 720, 2880, 2880, .7)),
        new("tetanus", "Clostridium tetani", ExposureRoute.Skin, new("tetanus", 1440, 4320, 2880, .75)),
        new("skin-fungal", "Environmental skin fungi", ExposureRoute.Skin, new("skin-fungal", 1, 5400, 3600, .4))
    ]);
    public static bool Enteric(string id) => id == "water-enteric" || All.Any(p => p.Id == id && p.Route == ExposureRoute.Ingestion);
    public static bool Fecal(string id) => Enteric(id) && id != "trichinosis";
    public static bool Wound(string id) => id is "wound-local" or "anthrax" or "tetanus" or "pasteurella" or "sporotrichosis" || All.Any(p => p.Id == id && p.Route == ExposureRoute.OpenWound);
    public static bool Airborne(string id) => All.Any(p => p.Id == id && p.Route == ExposureRoute.Airborne);
    public static IReadOnlyDictionary<string, DiseaseDefinition> Definitions { get; } = All.Select(p => p.Disease)
        .Concat([new("water-enteric", 300, 1800, 1500, .6), new("wound-local", 600, 3600, 2700, .5)])
        .Select(d => d with { IncubationSeconds = d.IncubationSeconds * 30, SymptomaticSeconds = d.SymptomaticSeconds * 30, RecoverySeconds = d.RecoverySeconds * 30 })
        .ToDictionary(d => d.Id, StringComparer.Ordinal);
}
