using Seraph.Core.Framework;

namespace Seraph.World;

internal static class PollutionText
{
    private static string Agent(string id, System.Func<string, object[], string> text)
        => Pathogens.Definitions.ContainsKey(id) ? text("pathogen-name-" + id, []) : id;

    internal static string[] Water(WaterBatch water, System.Func<string, object[], string> text, double servingL = .25, bool includeWaste = true)
    {
        // Keep ordinary water information actionable without repeating every
        // internal reservoir or deferred waste calculation.
        var biological = water.TotalEntericHazardPerL;
        var advice = water.SaltGramsPerL > 5 ? "avoid"
            : water.Turbidity > .15 ? "settle-boil"
            : biological > 0 || water.Unverified ? "boil" : "ready";
        string N(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var status = water.SaltGramsPerL > 5 ? "avoid"
            : biological > 0 || water.Turbidity > .15 || water.Unverified ? "treat" : "ready";
        var risk = biological <= 0 ? water.Unverified ? "unknown" : "none" : biological < .02 ? "low"
            : biological < .1 ? "moderate" : biological < .25 ? "high" : "very-high";
        var lines = new[] { text("water-safety-status", [text("water-status-" + status, [])]),
            text("water-microbial-load", [N(biological)]),
            text("water-quality-risk", [text("water-risk-" + risk, [])]) };
        return lines.Concat([text("water-drinking-advice", [text("water-advice-" + advice, [])])]).ToArray();
    }

    internal static string MapWaterStatus(PollutionSnapshot sample, System.Func<string, object[], string> text)
    {
        if (sample.FreshWater <= 0 && sample.SaltWater <= 0)
            return text("water-status-no-water", []);
        var levels = Levels(sample);
        var status = sample.SaltWater > 0 ? "avoid" : levels.Water >= .6 ? "danger"
            : levels.Water > .15 ? "treat" : "ready";
        return text("water-status-" + status, []);
    }

    internal static PollutionLevels Levels(PollutionSnapshot sample)
    {
        var local = PollutionModel.Levels(sample.Load);
        return new(Math.Max(local.Water, sample.Region.SurfacePathogens),
            Math.Max(local.Smoke, sample.Region.Air.Total), Math.Max(local.BiologicalAir, sample.Region.AirbornePathogens));
    }

    internal static string Sources(PollutionSnapshot snapshot)
    {
        var count = snapshot.SourceCount >= 0 ? snapshot.SourceCount
            : snapshot.Manure + snapshot.Fires + snapshot.Carrion + snapshot.Animals.Values.Sum();
        return Math.Max(0, count).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static IEnumerable<string> Region(RegionalEnvironment r, RegionalSources sources, System.Func<string, object[], string> text)
    {
        string P(double x) => x.ToString("P0");
        yield return text("pollution-gases", [P(r.Air.CO), P(r.Air.PM25), P(r.Air.PM10), P(r.Air.SO2)]);
        yield return text("pollution-organic-gases", [P(r.Air.NH3), P(r.Air.CH4), P(r.Air.H2S)]);
        yield return text("pollution-water-pathogens", [P(r.SurfacePathogens), P(r.GroundPathogens)]);
        foreach (var agent in r.SurfaceAgents.Keys.Concat(r.SoilAgents.Keys).Concat(r.GroundAgents.Keys).Distinct().Order())
            yield return text("pollution-agent-reservoirs", [Agent(agent, text), r.SurfaceAgents.GetValueOrDefault(agent).ToString("0.####"),
                r.SoilAgents.GetValueOrDefault(agent).ToString("0.####"), r.GroundAgents.GetValueOrDefault(agent).ToString("0.####")]);
        yield return text("pollution-soil", [P(r.SoilPathogens), P(r.SoilNitrogen), P(r.SoilPhosphorus)]);
        yield return text("pollution-land", [P(r.OrganicMatter), P(r.Erosion), P(r.Ecology)]);
        yield return text("pollution-forest", [P(sources.Forest), P(r.ForestLoss), P(r.CarbonSink)]);
        yield return text("pollution-ecology-sources", [P(sources.Cultivation), sources.Grazing.ToString("0.0"), sources.Wildlife, P(r.WildlifeLoss)]);
        yield return text("pollution-hydrology", [P(r.GroundwaterStress), P(r.RechargeMultiplier)]);
    }
}
