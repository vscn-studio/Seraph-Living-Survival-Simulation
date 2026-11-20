using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class ExposureBarrierChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double a, double b) => Check(Math.Abs(a-b) < 1e-8, $"Expected {a}, got {b}");
    private static Dictionary<string,double> Load(string id, double amount) => new() { [id] = amount };
    public static void CapacityAndClearance()
    {
        var k = new FrameworkKernel(); var start = FrameworkState.Create("barrier-tests");
        var s = ExposureBarriers.Add(k,start,ExposureRoute.Ingestion,Load("ecoli",60),"food");
        s = ExposureBarriers.Add(k,s,ExposureRoute.Ingestion,Load("giardia",40),"water");
        Check(s.Diseases.Select(d => d.Pathogen).Order().SequenceEqual(new[] { "ecoli", "giardia" }),
            "Partial intestinal protection lost verified species identity.");
        Near(35,s.Barriers.Ingested.Values.Sum());
        Near(40 * ExposureBarriers.IngestionPassThrough,s.Barriers.Ledger.Last().Overflow);
        // Existing full buckets remain valid and still deliver all of the next dose.
        s = s with { Barriers = s.Barriers with { Ingested = new Dictionary<string,double> { ["ecoli"] = 60, ["giardia"] = 40 } } };
        s = ExposureBarriers.Add(k,s,ExposureRoute.Ingestion,Load("salmonella",5),"food");
        Check(s.Diseases.Any(d => d.Pathogen == "salmonella"), "Barrier overflow lost identity.");
        Near(5,s.Barriers.Ledger.Last().Overflow);
        var split = s;
        for (var i=0;i<8;i++) split = ExposureBarriers.Advance(k,split,1);
        var whole = ExposureBarriers.Advance(k,s,8);
        Near(whole.Barriers.Ingested["ecoli"],split.Barriers.Ingested["ecoli"]);
        Near(100-5*8d/3600,whole.Barriers.Ingested.Values.Sum());
        Near(5 * 8d / 3600 * .25, whole.Blood.WasteMl - s.Blood.WasteMl);
        Near(whole.Blood.WasteMl, split.Blood.WasteMl);
        var skin = ExposureBarriers.Add(k,start,ExposureRoute.Skin,Load("anthrax",200),"soil");
        Near(200,skin.Barriers.Skin["anthrax"]); // No hidden 100-per-species concentration cap.
        var wound = k.AddWound(skin,WoundKind.Cut,.2,.001,"left-hand").State;
        var reduced = ExposureBarriers.Advance(k,wound,0);
        Near(wound.Blood.WasteMl, reduced.Blood.WasteMl);
        Near(75,reduced.Barriers.Skin.Values.Sum());
        Check(reduced.Diseases.Single().Pathogen == "anthrax", "Open bleeding wound failed to reduce skin protection.");
        var wash = ExposureBarriers.Wash(s);
        Near(100,wash.Barriers.Ingested.Values.Sum()); // Bathing is not intestinal treatment.
        Near(20,ExposureBarriers.Wash(skin).Barriers.Skin["anthrax"]);
        Near(skin.Blood.WasteMl, ExposureBarriers.Wash(skin).Blood.WasteMl);
        var loaded = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(reduced))!;
        Check(loaded.Validate() is null && loaded.Barriers.Skin["anthrax"] == 75, "Barrier save lost load.");
        var entericSkin = ExposureBarriers.Add(k,start,ExposureRoute.Skin,Load("ecoli",250),"skin-contact");
        Check(entericSkin.Diseases.Length == 0 && entericSkin.Barriers.Skin["ecoli"] == 200,
            "Skin overflow swallowed an enteric agent or lost carriage.");
        var tetanus = ExposureBarriers.Add(k,start,ExposureRoute.Skin,Load("tetanus",250),"skin-contact");
        Check(tetanus.Diseases.Length == 0, "Intact skin admitted wound-only tetanus.");
    }
    public static void AmbientAndHistory()
    {
        var k = new FrameworkKernel(); var source = EnvironmentSample.Neutral with {
            DiseaseSources = new Dictionary<string,double> { ["qfever"] = .01, ["histoplasmosis"] = .01 }, DampSkin = true };
        var start = FrameworkState.Create("ambient") with { Dirtiness=.8, Barriers = new() { Ambient = new Dictionary<string,AmbientExposureClock> {
            ["qfever"] = new(0,.00001), ["histoplasmosis"] = new(0,.00002) } } };
        var whole = DiseaseExposure.Advance(k,start,source,8,new(),new HashSet<string>());
        var split = start;
        for (var i=0;i<8;i++) {
            split = DiseaseExposure.Advance(k,split,source,1,new(),new HashSet<string>());
            split = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(split))!;
        }
        Check(whole.Diseases.Length == 0 && whole.Barriers.Skin.Count == 0, "Trace contact or damp skin invented disease.");
        foreach (var id in whole.Barriers.Inhaled.Keys) Near(whole.Barriers.Inhaled[id],split.Barriers.Inhaled[id]);
        foreach (var id in whole.Barriers.Ambient.Keys) {
            Near(whole.Barriers.Ambient[id].Hazard,split.Barriers.Ambient[id].Hazard);
            Check(whole.Barriers.Ambient[id].Threshold == split.Barriers.Ambient[id].Threshold
                && whole.Barriers.Ambient[id].RandomState == split.Barriers.Ambient[id].RandomState, "Ambient partition/reload rerolled random state.");
        }
        var clean = DiseaseExposure.Advance(k,start with { Dirtiness=0 },source,8,new(),new HashSet<string>());
        foreach (var id in whole.Barriers.Inhaled.Keys) Near(whole.Barriers.Inhaled[id], clean.Barriers.Inhaled[id]);
        var exposed = k.Expose(start,Pathogens.Definitions["giardia"],1e-7,1).State;
        var threshold = exposed.Diseases.Single().InfectionThreshold;
        var cleared = k.ProgressDiseases(exposed,Pathogens.Definitions,8,new HashSet<string> { "giardia" }).State;
        Check(cleared.Diseases.Length == 0 && cleared.ImmuneMemory.Count == 0, "Trace exposure survived clearance or granted memory.");
        cleared = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(cleared))!;
        var again = k.Expose(cleared,Pathogens.Definitions["giardia"],1e-7,1).State;
        Near(threshold,again.Diseases.Single().InfectionThreshold);
        Check(cleared.RandomState == again.RandomState, "Cleared exposure rerolled its infection threshold.");
        var stronger = k.Expose(start,Pathogens.Definitions["ecoli"],.001,1).State;
        var progressed = k.ProgressDiseases(stronger,Pathogens.Definitions,8,new HashSet<string> { "ecoli" }).State;
        Near(stronger.Diseases.Single().CumulativeHazard * Math.Exp(-Math.Log(2)*8/21600),progressed.Diseases.Single().CumulativeHazard);
    }
}
