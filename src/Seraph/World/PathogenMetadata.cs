using System.Text.Json;
using Seraph.Core.Framework;
using Vintagestory.API.Common;

namespace Seraph.World;

/// <summary>Concentration per item, retained by native item cloning/splitting and equality checks.</summary>
internal static class PathogenMetadata
{
    internal const string Key = "vsslssmod:pathogens-v1";
    internal static IReadOnlyDictionary<string, double> Read(ItemStack stack)
    {
        var json = stack.Attributes.GetString(Key, "{}");
        if (json.Length > 4096) return PathogenLoads.Empty;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            return PathogenLoads.Valid(values) ? values! : PathogenLoads.Empty;
        }
        catch (JsonException) { return PathogenLoads.Empty; }
    }
    internal static void Write(ItemStack stack, IReadOnlyDictionary<string, double> values)
    {
        if (!PathogenLoads.Valid(values)) throw new ArgumentException("Invalid surface pathogen metadata.");
        stack.Attributes.SetString(Key, JsonSerializer.Serialize(values.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value)));
    }
    internal static IReadOnlyDictionary<string, double> Shed(FrameworkState state)
        => state.Diseases.Where(d => Pathogens.Fecal(d.Pathogen) && d.Phase is DiseasePhase.Symptomatic or DiseasePhase.Recovering)
            .ToDictionary(d => d.Pathogen, d => Math.Clamp(d.Burden, .1, 1));
}
