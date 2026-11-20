using System.Text.Json;
using Seraph.Core.Framework;

internal static class WaterRiskChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static double Weekly(string source, double immunity = 1, double memory = 0)
    {
        var kernel = new FrameworkKernel();
        var state = FrameworkState.Create("weekly-water") with { ImmuneReserve = immunity,
            InfectionThresholdHistory = new Dictionary<string,double> { ["water-enteric"] = 100 },
            ImmuneMemory = memory > 0 ? new Dictionary<string,double> { ["water-enteric"] = memory } : PathogenLoads.Empty };
        var water = SurfaceWater.Apply(new() { VolumeL = 1 }, source, new());
        var incoming = new Dictionary<string,double> { ["water-enteric"] = ExposureBarriers.ContactUnits(water.EntericHazardPerL, .25) };
        var peak = 0d;
        // Eight 250 mL drinks, three game hours apart, for seven days. Real kernels
        // clear the route bucket and exposed hazard between every drink.
        for (var drink = 0; drink < 56; drink++)
        {
            state = ExposureBarriers.Add(kernel, state, ExposureRoute.Ingestion, incoming, "water");
            peak = Math.Max(peak, state.Diseases.FirstOrDefault()?.CumulativeHazard ?? 0);
            if (drink == 27) state = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(state))!;
            for (var elapsed = 0; elapsed < 10800; elapsed += 8)
            {
                state = ExposureBarriers.Advance(kernel, state, 8);
                state = kernel.ProgressDiseases(state, Pathogens.Definitions, 8).State;
            }
        }
        // Infection thresholds are persistent exponential draws. The probability
        // of crossing at any point is their CDF at the maximum, not summed decayed doses.
        return 1 - Math.Exp(-peak);
    }
    internal static void Drinking()
    {
        var pond = Weekly("pond");
        var spring = Weekly("spring");
        Check(pond is >= .05 and <= .15, $"Seven-day pond risk outside target: {pond:P2}");
        Check(spring is > 0 and < .01, $"Seven-day spring risk outside target: {spring:P2}");
        var single = FrameworkState.Create("single-water") with {
            InfectionThresholdHistory = new Dictionary<string,double> { ["water-enteric"] = 100 } };
        single = ExposureBarriers.Add(new(), single, ExposureRoute.Ingestion,
            new Dictionary<string,double> { ["water-enteric"] = .15 * .25 * ExposureBarriers.UnitsPerHazard }, "water");
        var singleRisk = 1 - Math.Exp(-single.Diseases.Single().CumulativeHazard);
        Check(singleRisk < .03 && singleRisk < pond, "A single pond drink is too dangerous or does not accumulate.");
        var vulnerable = single with { Diseases = [], ImmuneReserve = .4, Barriers = new() };
        vulnerable = ExposureBarriers.Add(new(), vulnerable, ExposureRoute.Ingestion,
            new Dictionary<string,double> { ["water-enteric"] = 3.75 }, "water");
        Check(vulnerable.Diseases.Single().CumulativeHazard > single.Diseases.Single().CumulativeHazard,
            "Immunity no longer protects against ingestion.");
        var clean = ExposureBarriers.Add(new(), FrameworkState.Create("boiled-water"), ExposureRoute.Ingestion,
            PathogenLoads.WithGenericEnteric(WaterHeating.AtTemperature(new() { VolumeL = 1, EntericHazardPerL = .15, Turbidity = .8 }, 100).PathogensPerL, 0), "water");
        Check(clean.Diseases.Length == 0, "Boiled muddy water invented exposure.");
        Check(SurfaceWater.TurbidityHazard(1) == .03 && SurfaceWater.TurbidityHazard(0) == 0,
            "Extra turbidity does not have a bounded, generic effect.");
        Console.WriteLine($"INFO 2 L/day for 7 days: pond {pond:P2}, spring {spring:P2}; one 250 mL pond drink {singleRisk:P2}");
    }
    internal static void PondTransport()
    {
        var polluted = new PondContamination { Generic = 1, Agents = new Dictionary<string,double> { ["giardia"] = .4 } };
        var cells = Enumerable.Range(0, 64).Select(i => i == 0 ? polluted : new PondContamination()).ToArray();
        var mean = PondContaminationModel.Mean(cells);
        PondContamination[] Step(PondContamination[] old, double hours, PondContamination[]? inputs = null)
        {
            inputs ??= old.Select(_ => new PondContamination()).ToArray();
            var m = PondContaminationModel.Mean(old); var flow = PondContaminationModel.Mean(inputs);
            return old.Select((c, i) => PondContaminationModel.AdvanceCell(c, m, inputs[i], flow, hours)).ToArray();
        }
        void Near(double a, double b) => Check(Math.Abs(a - b) < 1e-9, $"Pond transport expected {a}, got {b}");
        var mixed = Step(cells, 12);
        Near(.5, mixed.Sum(c => c.Generic));
        Near(.2, mixed.Sum(c => c.Agents.GetValueOrDefault("giardia")));
        Check(mixed.All(c => c.Generic > 0 && c.Agents.GetValueOrDefault("giardia") > 0), "Pollution did not reach the entire closed pond.");
        Check(mixed[0].Generic > mixed[63].Generic, "Pond mixing was instantaneous.");
        var split = Step(Step(cells, 4), 8);
        for (var i = 0; i < cells.Length; i++) Near(mixed[i].Generic, split[i].Generic);
        var inputs = cells.Select((_, i) => i == 0 ? polluted : new PondContamination()).ToArray();
        var continuous = Step(cells, 4, inputs);
        var partition = Step(Step(cells, 1, inputs), 3, inputs);
        for (var i = 0; i < cells.Length; i++) Near(continuous[i].Generic, partition[i].Generic);
        Check(continuous.Sum(c => c.Generic) > 1, "Continuous pollution failed to accumulate.");
        var twice = Step(cells, 4, inputs.Select(c => c.Scale(2)).ToArray());
        Check(twice.Sum(c => c.Generic) > continuous.Sum(c => c.Generic), "More sources did not increase concentration.");
        var large = Enumerable.Repeat(new PondContamination(), 128).ToArray();
        var largeInput = large.Select((_, i) => i == 0 ? polluted : new PondContamination()).ToArray();
        Check(PondContaminationModel.Mean(Step(large, 4, largeInput)).Generic < PondContaminationModel.Mean(continuous).Generic,
            "Larger ponds failed to dilute the same emission.");
        var saved = JsonSerializer.Deserialize<PondContamination[]>(JsonSerializer.Serialize(continuous))!;
        Near(Step(continuous, 2).Sum(c => c.Generic), Step(saved, 2).Sum(c => c.Generic));
        // A physical partition retains each side's own stock; recomputing each mean cannot clone it.
        var left = Step(continuous.Take(32).ToArray(), 2);
        var right = Step(continuous.Skip(32).ToArray(), 2);
        Near(Step(continuous, 2).Sum(c => c.Generic), left.Concat(right).Sum(c => c.Generic));
        Near(mean.Generic, 1d / 64);
    }
}
