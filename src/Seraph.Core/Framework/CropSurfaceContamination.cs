namespace Seraph.Core.Framework;

public static class CropSurfaceContamination
{
    /// <summary>Constant cached source; exact 24-game-hour half-life, independent of callback partition.</summary>
    public static IReadOnlyDictionary<string, double> Advance(IReadOnlyDictionary<string, double> old,
        RegionalEnvironment region, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return old;
        var k = Math.Log(2) / 86400;
        var retained = Math.Exp(-k * seconds);
        var input = PathogenLoads.Add(region.SoilAgents, region.SurfaceAgents);
        var generic = region.SoilPathogens + region.SurfacePathogens;
        if (generic > 0) input = PathogenLoads.Add(input, new Dictionary<string, double> { ["water-enteric"] = generic });
        return PathogenLoads.Add(PathogenLoads.Scale(old, retained), PathogenLoads.Scale(input, (1 - retained) / k / 3600 * .02));
    }
}
