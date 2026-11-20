using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class ClinicalWarningChecks
{
    private static void Require(bool condition) { if (!condition) throw new Exception("Clinical warning contract failed."); }
    internal static void Stages()
    {
        var body = BodyState.Create(); var p = BodyProfile.Default;
        var f = FrameworkState.Create("warning-stages");
        foreach (var (stamina, stage) in new[] { (.200001, 0), (.2, 1), (.050001, 1), (.05, 2), (0d, 2) })
            Require((ClinicalWarningPolicy.Evaluate(f with { Stamina = stamina }, body, p)
                .SingleOrDefault(w => w.Key == "exertion-stamina")?.Stage ?? 0) == stage);
        foreach (var (ratio, stage) in new[] { (.799999, 0), (.8, 1), (.949999, 1), (.95, 2), (1d, 2) })
            Require(ClinicalWarningPolicy.Evaluate(f with { Blood = new() { LactateG = ratio * BloodCirculation.LactateCapacityG(body, p) } }, body, p)
                .Single(w => w.Key == "exertion-lactate").Stage == stage);
        ClinicalWarning[] Ketones(double level) => ClinicalWarningPolicy.Evaluate(
            f with { Clinical = f.Clinical with { Ketosis = level } }, body, p).Where(w => w.Key == "ketosis").ToArray();
        foreach (var (level, stage) in new[] { (.249999, 0), (.25, 1), (.599999, 1), (.6, 2), (1d, 2) })
            Require((Ketones(level).SingleOrDefault()?.Stage ?? 0) == stage);
        var kt = new ClinicalWarningTracker(); kt.Seed([]);
        Require(kt.Advance(Ketones(.25), 2).Single().Stage == 1);
        Require(kt.Advance(Ketones(.5), 30).Length == 0);
        Require(kt.Advance(Ketones(.6), 2).Single().Stage == 2);
        Require(kt.Advance(Ketones(.4), 30).Length == 0);
        Require(kt.Advance(Ketones(0), 10).Length == 0);
        Require(kt.Advance(Ketones(.25), 2).Single().Stage == 1);
        var empty = f with { Stamina = 0, ExertionExhausted = true, Blood = new() { LactateG = BloodCirculation.LactateCapacityG(body, p) } };
        var exertion = ClinicalWarningPolicy.Evaluate(empty, body, p).Where(w => w.Stage > 0).ToArray();
        Require(exertion.Count(w => w.Key.StartsWith("exertion-")) == 1
            && exertion.Any(w => w.Key == "exertion-exhausted")
            && exertion.All(w => w.Key is not ("lactate-overload" or "overexertion-stamina" or "overexertion-lactate")));
        var tracker = new ClinicalWarningTracker(); tracker.Seed([]);
        ClinicalWarning[] Reserves(double stamina, double lactate = 0, bool exhausted = false) => ClinicalWarningPolicy.Evaluate(
            f with { Stamina = stamina, Blood = new() { LactateG = lactate }, ExertionExhausted = exhausted }, body, p);
        Require(tracker.Advance(Reserves(.2), 2).Single().Stage == 1);
        Require(tracker.Advance(Reserves(.05), 2).Single().Stage == 2);
        Require(tracker.Advance(Reserves(.19), 30).Length == 0);
        Require(tracker.Advance(Reserves(.05), 30).Length == 0); // short rest and work remain one episode
        var exhaustedWarnings = tracker.Advance(Reserves(0, 100, true), 2);
        Require(exhaustedWarnings.Length == 1
            && exhaustedWarnings.Count(w => w.Key == "exertion-exhausted") == 1
            && exhaustedWarnings.All(w => w.Key != "pain"));
        Require(tracker.Advance(Reserves(.4, 80, true), 30).Length == 0);
        Require(tracker.Advance(Reserves(.35, 59), 10).Length == 0);
        Require(tracker.Advance(Reserves(.2), 2).Single().Stage == 1);
        foreach (var (ratio, stage) in new[] { (.399999, 0), (.4, 1), (.55, 2), (.7, 3), (.85, 4), (1.2, 4) })
        {
            var s = BloodCirculation.AddWaste(f, body, p, BloodCirculation.WasteCapacityMl(body, p) * ratio);
            var warning = ClinicalWarningPolicy.Evaluate(s, body, p).SingleOrDefault(w => w.Key == "blood-toxicity");
            Require((warning?.Stage ?? 0) == stage);
            Require(warning?.Urgent == (stage == 4) || warning is null);
            Require(Math.Abs(s.Blood.Toxicity - Math.Clamp((ratio - .4) / .45, 0, 1)) < 1e-12);
        }
        foreach (var (level, stage) in new[] { (.6, 1), (.8, 2), (.9, 3), (1d, 4) })
            Require(ClinicalWarningPolicy.Evaluate(f with { Fatigue = level }, body, p).Single(w => w.Key == "exhaustion").Stage == stage);
        foreach (var (level, stage) in new[] { (.7, 1), (.9, 2), (.95, 3), (1d, 4) })
            Require(ClinicalWarningPolicy.Evaluate(f, body with { SleepPressure = level }, p).Single(w => w.Key == "drowsy").Stage == stage);
        var disease = f with { Diseases = [new() { Pathogen = "malaria", Phase = DiseasePhase.Incubating, Burden = .5 }] };
        Require(ClinicalWarningPolicy.Evaluate(disease, body, p).All(w => w.Key != "malaria"));
        disease = disease with { Diseases = [disease.Diseases[0] with { Phase = DiseasePhase.Symptomatic }] };
        Require(ClinicalWarningPolicy.Evaluate(disease, body, p).Any(w => w.Key == "malaria"));
        foreach (var (reserve, temp, expected) in new[] { (8000d, 38d, false), (800d, 38d, false),
            (799d, 37.5, true), (799d, 37.499, false), (8000d, 39.5, true) })
        {
            var warm = body with { BodyWaterL = p.ReferenceBodyWaterL - (8000 - reserve) / 1000, CoreTemperatureC = temp };
            Require(ClinicalWarningPolicy.Evaluate(f, warm, p).Any(w => w.Key == "heat-illness") == expected);
        }
    }
    internal static void Episodes()
    {
        var t = new ClinicalWarningTracker(); t.Seed([]);
        ClinicalWarning[] a = [new("pain", 1)];
        Require(t.Advance(a, .1).Length == 1);
        for (var i = 0; i < 500; i++) Require(t.Advance(a, .1).Length == 0);
        a = [new("pain", 2)]; Require(t.Advance(a, 0).Length == 1);
        a = [new("pain", 3)]; Require(t.Advance(a, .5).Length == 0);
        Require(t.Advance(a, 0).Length == 0); // paused presentation clock
        Require(t.Advance(a, 1.5).Single().Stage == 3);
        Require(t.Advance([], 9).Length == 0);
        Require(t.Advance([new("pain", 1)], 0).Length == 0); // short recovery must not spam
        t.Advance([], 10);
        Require(t.Advance([new("pain", 1)], 0).Length == 1);
        t.Seed([new("pain", 3)]); // relog establishes a silent baseline
        Require(t.Advance([new("pain", 3)], 100).Length == 0);
        Require(t.Advance([new("pain", 4)], 0).Single().Stage == 4);
    }
    internal static void Batches()
    {
        var t = new ClinicalWarningTracker(); t.Seed([]);
        t.Advance([new("pain", 1)], 0);
        Require(t.Advance([new("pain", 1), new("insect-carrier", 1)], .1, 1).Length == 0);
        var batch = t.Advance([new("pain", 1), new("insect-carrier", 1), new("sepsis", 1, true)], 0, 2);
        Require(batch.Length == 3 && batch[0].Key == "sepsis" && batch.Single(w => w.Key == "insect-bite").Count == 3);
        t.Advance([new("pain", 2)], .1); // queued until interval elapses
        Require(t.Advance([], 2).Length == 0); // vanished condition is not displayed later
        t.Advance([], 0, 1); t.Reset();
        Require(t.Advance([], 20).Length == 0);
    }
}
