using System.Text.Json;
using Seraph.Core.Framework;

internal static class PathogenChecks
{
    private static void Check(bool value) { if (!value) throw new Exception("Pathogen conservation/memory failed."); }
    public static void Conservation()
    {
        var a = new WaterBatch { VolumeL = 2, PathogensPerL = new Dictionary<string, double> { ["giardia"] = .4 } };
        var b = new WaterBatch { VolumeL = 6, EntericHazardPerL = .2, PathogensPerL = new Dictionary<string, double> { ["ecoli"] = .8 } };
        var mixed = a.Mix(b);
        Check(Math.Abs(mixed.PathogensPerL["giardia"] * 8 - .8) < 1e-12);
        Check(Math.Abs(mixed.PathogensPerL["ecoli"] * 8 - 4.8) < 1e-12);
        Check(mixed.PathogensPerL.Count == 2 && Math.Abs(mixed.EntericHazardPerL - .15) < 1e-12);
        var filtered = WaterFiltration.Process(mixed, true, true);
        Check(Math.Abs(filtered.PathogensPerL["ecoli"] / mixed.PathogensPerL["ecoli"] - .15) < 1e-12);
        Check(WaterHeating.AtTemperature(mixed, 100).PathogensPerL.Count == 0);
        var evaporated = WaterHeating.Evaporate(mixed, 4);
        Check(Math.Abs(evaporated.PathogensPerL["ecoli"] * 4 - 4.8) < 1e-12);
        var restored = JsonSerializer.Deserialize<WaterBatch>(JsonSerializer.Serialize(mixed))!;
        Check(restored.Validate() is null && restored.PathogensPerL.Count == 2);
        Check(new WaterBatch { VolumeL = 1, EntericHazardPerL = .2 }.PathogensPerL.Count == 0);
    }
    public static void Memory()
    {
        var kernel = new FrameworkKernel();
        var def = new DiseaseDefinition("giardia", 1, 1, 1, .5);
        var defs = new Dictionary<string, DiseaseDefinition> { [def.Id] = def };
        var exposed = kernel.Expose(new(), def, .0001, 1).State;
        var threshold = exposed.Diseases[0].InfectionThreshold;
        var restored = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(exposed))!;
        Check(restored.RandomState == exposed.RandomState && restored.Diseases[0].InfectionThreshold == threshold);
        var oldHazard = exposed.Diseases[0].CumulativeHazard;
        for (var t = 0; t < 21600; t += 8) exposed = kernel.ProgressDiseases(exposed, defs, 8).State;
        Check(Math.Abs(exposed.Diseases[0].CumulativeHazard - oldHazard / 2) < 1e-12 && exposed.ImmuneMemory.Count == 0);
        var infected = kernel.Expose(new(), def, 100, 1).State;
        var recovered = kernel.ProgressDiseases(infected, defs, 8).State;
        Check(recovered.Diseases.Length == 0 && recovered.ImmuneMemory["giardia"] == .5);
        var protectedExposure = kernel.Expose(recovered, def, .0001, 1).State;
        Check(Math.Abs(protectedExposure.Diseases[0].CumulativeHazard - oldHazard / 2) < 1e-12);
        for (var t = 0; t < 259200; t += 8) recovered = kernel.ProgressDiseases(recovered, defs, 8).State;
        Check(Math.Abs(recovered.ImmuneMemory["giardia"] - .25) < 1e-10);
    }

    public static void GenericEntericDeduplication()
    {
        var identified = new Dictionary<string, double> { ["ecoli"] = .7 };
        var combined = PathogenLoads.WithGenericEnteric(identified, .5);
        Check(combined.Count == 1 && combined["ecoli"] == .7);
        var genericOnly = PathogenLoads.WithGenericEnteric(PathogenLoads.Empty, .5);
        Check(genericOnly.Count == 1 && genericOnly["water-enteric"] == .5);
        var overlappingGeneric = PathogenLoads.WithGenericEnteric(
            new Dictionary<string, double> { ["water-enteric"] = .4 }, .5);
        Check(overlappingGeneric.Count == 1 && overlappingGeneric["water-enteric"] == .5);
        var mixed = PathogenLoads.WithGenericEnteric(new Dictionary<string, double> { ["wound-staph"] = .2 }, .5);
        Check(mixed.Count == 2 && mixed["water-enteric"] == .5);
    }
}
