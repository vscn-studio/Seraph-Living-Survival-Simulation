using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Native blending remains authoritative. Never remove another mod's sources to inspect them.</summary>
internal static class NativeStatInputs
{
    internal const string BodySource = "vsslssmod:function";
    private static readonly FieldInfo? HealthModifiers = typeof(EntityBehaviorHealth)
        .GetField("maxHealthModifiers", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static double Multiplier(Entity entity, string category)
    {
        var value = entity.Stats?.GetBlended(category) ?? 1;
        return float.IsFinite(value) && value >= 0 ? value : 1;
    }

    internal static double PhysiologyMultiplier(Entity entity, string category)
    {
        var value = Multiplier(entity, category);
        return value <= 100 ? value : 1;
    }

    internal static (string Source, float Value, float Weight)[] Sources(EntityStats stats, string category) =>
        stats.FirstOrDefault(p => p.Key == category).Value?.ValuesByKey
            .Where(p => p.Key != "base" && p.Key != BodySource && float.IsFinite(p.Value.Value) && float.IsFinite(p.Value.Weight))
            .Select(p => (p.Key, p.Value.Value, p.Value.Weight)).ToArray() ?? [];

    internal static void SetOwned(Entity entity, string category, string source, float? value)
    {
        var stat = entity.Stats.FirstOrDefault(p => p.Key == category).Value;
        var existing = stat?.ValuesByKey.FirstOrDefault(p => p.Key == source).Value;
        if (value is { } next)
        {
            if (existing is not null && existing.Value == next && !existing.Persistent) return;
            entity.Stats.Set(category, source, next, false);
        }
        else if (existing is not null) entity.Stats.Remove(category, source);
    }

    internal static double ExternalHealth(Entity entity)
    {
        var blended = entity.Stats?.GetBlended("maxhealthExtraPoints") ?? 1;
        var points = float.IsFinite(blended) ? (double)blended - 1 : 0;
        // The obsolete public MaxHealthModifiers property is not the field used by native UpdateMaxHealth.
        if (entity.Properties is not null && entity.World is not null
            && entity.GetBehavior<EntityBehaviorHealth>() is { } health
            && HealthModifiers?.GetValue(health) is Dictionary<string, float> modifiers)
            foreach (var (source, value) in modifiers)
                if (source != "nutrientHealthMod" && float.IsFinite(value)) points += value;
        return double.IsFinite(points) ? Math.Clamp(points, -90, 1000000) : 0;
    }

    internal static string SourceLabel(string source) => source switch {
        "sitWalkingBonus" => "native-source-leveling",
        "jaunt:walkspeed" => "native-source-jaunt",
        _ when source.StartsWith("carry", StringComparison.OrdinalIgnoreCase) => "native-source-carry",
        _ => "native-source-other"
    };
}
