using Vintagestory.API.Common;

namespace Seraph.World;

internal readonly record struct PollutionPlant(double LeafUnits, bool Aquatic, bool Dormant, string Species)
{
    internal double ActiveUnits => Dormant ? 0 : LeafUnits;
    internal double SootUnits => LeafUnits * (Dormant ? .25 : 1);
}

internal static class PollutionVegetation
{
    private static System.Func<Block, bool>? ecology;
    private static System.Func<Block, string>? species;
    private static System.Reflection.MethodInfo? habitat;
    private static int assemblies;
    private static void Bind()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        if (assemblies == loaded.Length) return;
        assemblies = loaded.Length;
        var helper = loaded.Select(a => a.GetType("WildFarming.Ecosystem.PlantCodeHelper")).FirstOrDefault(t => t is not null);
        if (helper is null) return;
        try
        {
            ecology = helper.GetMethod("IsEcologyPlant", [typeof(Block)])?.CreateDelegate<System.Func<Block, bool>>();
            species = helper.GetMethod("ResolveEcologySpecies", [typeof(Block)])?.CreateDelegate<System.Func<Block, string>>();
            habitat = helper.GetMethod("GetEcologyHabitat", [typeof(Block)]);
        }
        catch { ecology = null; species = null; habitat = null; }
    }
    internal static PollutionPlant Classify(Block block)
    {
        Bind();
        var path = block.Code?.Path ?? "";
        // Use ecosystemflora's public participant/species/habitat contract. Do not
        // count trunks, dead branches, mushrooms or decorative artificial foliage.
        if (new[] { "dead", "dry", "log-", "trunk", "mushroom", "artificial", "ornament" }.Any(path.Contains)) return default;
        var participates = false;
        string? ecologySpecies = null, ecologyHabitat = null;
        try
        {
            participates = ecology?.Invoke(block) == true;
            ecologySpecies = species?.Invoke(block);
            ecologyHabitat = habitat?.Invoke(null, [block])?.ToString();
        }
        catch { ecology = null; species = null; habitat = null; }
        var declared = block.Attributes?["ecologyParticipant"].AsBool(false) == true;
        var leaves = block.BlockMaterial == EnumBlockMaterial.Leaves;
        var living = declared || leaves || participates || block.BlockMaterial == EnumBlockMaterial.Plant
            || path.StartsWith("berrybush-", StringComparison.Ordinal) || path.StartsWith("crop-", StringComparison.Ordinal);
        if (!living) return default;
        var habitatName = ecologyHabitat ?? block.Attributes?["ecologyHabitat"].AsString("") ?? "";
        var aquatic = habitatName.Contains("aquatic", StringComparison.OrdinalIgnoreCase)
            || habitatName.Contains("water", StringComparison.OrdinalIgnoreCase)
            || new[] { "waterlily", "waterplant", "seaweed", "coontail", "papyrus", "brownsedge", "reed" }.Any(path.Contains);
        var juvenile = path.Contains("juvenile", StringComparison.Ordinal) || path.Contains("sapling", StringComparison.Ordinal)
            || path.Contains("-short-", StringComparison.Ordinal);
        var dormant = path.Contains("dormant", StringComparison.Ordinal) || path.Contains("leafless", StringComparison.Ordinal)
            || path.Contains("-winter", StringComparison.Ordinal) || path.Contains("harvested", StringComparison.Ordinal);
        var units = leaves ? 1d : .25;
        if (juvenile) units *= .25;
        if (path.Contains("potted", StringComparison.Ordinal) || path.Contains("flowerpot", StringComparison.Ordinal)) units = Math.Min(units, .25);
        return new(units, aquatic, dormant, ecologySpecies ?? block.Attributes?["ecologySpecies"].AsString(path) ?? path);
    }
}
