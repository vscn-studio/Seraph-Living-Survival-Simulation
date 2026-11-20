using System.Text.Json;

namespace Seraph.Core.Framework;

/// <summary>Release water contract. RC doses and missing identities are never converted.</summary>
public static class WaterQualityCompatibility
{
    public const int CurrentVersion = 1;
    public static WaterBatch? Read(JsonElement json)
    {
        var water = json.Deserialize<WaterBatch>(new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
        return water is not null && water.Validate() is null ? water with { ChemicalHazardPerL = 0 } : throw new InvalidDataException("Unsupported or invalid release water format.");
    }
    public static WaterBatch RequireCurrent(WaterBatch water)
        => water.Validate() is null ? water with { ChemicalHazardPerL = 0 } : throw new InvalidDataException("Unsupported or invalid release water format.");
}
