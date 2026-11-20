using Seraph.Core.Framework;

internal static class SoapCareChecks
{
    internal static void Run()
    {
        var state = new FrameworkState { Dirtiness = .8, SkinPathogens = new Dictionary<string, double> { ["skin-fungal"] = .8 },
            Wounds = [new Wound { EventSequence = 1, Kind = WoundKind.Cut, Healing = .2, Contamination = .6,
                Pathogens = new Dictionary<string, double> { ["wound-staph"] = .8 } },
                new Wound { EventSequence = 2, Kind = WoundKind.Fracture, Healing = .1, Contamination = .5 }],
            Diseases = [new DiseaseState { Pathogen = "wound-staph", Phase = DiseasePhase.Symptomatic, Burden = .5 }] };
        var clean = SoapCare.CleanAll(state, 100);
        Require(clean.Dirtiness == 0 && clean.CleanRemainingGameHours >= 24 && clean.SkinPathogens["skin-fungal"] == .4,
            "Soap did not clean skin soil and carried agents.");
        Require(clean.Wounds[0].Contamination == .3 && clean.Wounds[0].Pathogens["wound-staph"] == .4
            && clean.Wounds[0].CleanUntilBioSeconds == 1900, "Soap did not clean the open wound.");
        Require(clean.Wounds[1].Contamination == .5, "Soap changed a closed fracture.");
        Require(clean.Diseases.SequenceEqual(state.Diseases) && clean.Wounds[0].Healing == .2,
            "Soap cured an established infection or advanced wound healing.");
        Require(clean.Treatments.Length == 0, "Plain soap added a treatment effect.");
        Console.WriteLine("PASS soap cleans without curing infection or changing medicines");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
}
