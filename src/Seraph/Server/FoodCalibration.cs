using System.Text.Json.Nodes;
using JsonObject = Vintagestory.API.Datastructures.JsonObject;
using DataObject = System.Text.Json.Nodes.JsonObject;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Seraph.Server;

/// <summary>Calibrate acquisition before entities and presses copy their resolved definitions.</summary>
internal static class FoodCalibration
{
    internal static void Apply(ICoreAPI api)
    {
        var catalog = System.Text.Json.JsonSerializer.Deserialize<FoodCatalog>(
            api.Assets.Get(new AssetLocation("vsslssmod:config/foods.json")).ToText())!;
        var animals = 0;
        if (api.Side == EnumAppSide.Server)
            foreach (var entity in api.World.EntityTypes)
                if (CalibrateAnimal(entity, catalog)) animals++;
        var presses = 0;
        foreach (var item in api.World.Items)
        {
            if (item?.Code is null || item.Attributes?["juiceableProperties"].Exists != true) continue;
            var code = item.Code.ToString();
            if (code is "game:legume-soybean" or "game:vegetable-soybean")
            {
                var attributes = JsonNode.Parse(item.Attributes.ToString()!)!.AsObject();
                attributes.Remove("juiceableProperties");
                item.Attributes = JsonObject.FromJson(attributes.ToJsonString());
                continue;
            }
            if (!code.StartsWith("game:fruit-", StringComparison.Ordinal) && code != "cottageindustry:motleyapple") continue;
            if (catalog.Find(new ItemStack(item)) is not { PortionG: > 0 } profile) continue;
            var copy = JsonNode.Parse(item.Attributes.ToString()!)!.AsObject();
            copy["juiceableProperties"]!["litresPerItem"] = profile.PortionG * .7 / 1000;
            item.Attributes = JsonObject.FromJson(copy.ToJsonString());
            presses++;
        }
        api.Logger.Notification("[vsslssmod] Food calibration: {0} animal definitions, {1} fruit press yields; soybean milk production disabled.", animals, presses);
    }

    internal static bool CalibrateAnimal(EntityProperties entity, FoodCatalog catalog)
    {
        if (entity.Code?.Domain != "game" || !float.IsFinite(entity.Weight) || entity.Weight <= 0
            || entity.Server?.BehaviorsAsJsonObj is not { } behaviors) return false;
        var index = Array.FindIndex(behaviors, b => b["code"].AsString() == "harvestable");
        if (index < 0 || behaviors[index]["seraphMeatYield"].AsBool()) return false;
        var copy = JsonNode.Parse(behaviors[index].ToString()!)!.AsObject();
        if (copy["drops"] is not JsonArray drops) return false;
        var meats = new List<(DataObject Drop, double Mass, double Mean)>();
        foreach (var drop in drops.OfType<DataObject>())
        {
            var code = drop["code"]?.GetValue<string>();
            if (code is null) continue;
            if (!code.Contains(':')) code = entity.Code.Domain + ":" + code;
            if (code is not ("game:redmeat-raw" or "game:bushmeat-raw" or "game:poultry-raw" or "game:fish-raw")) continue;
            var profile = catalog.Find(new ItemStack(new Item { Code = new(code) }));
            var mean = drop["quantity"]?["avg"]?.GetValue<double>() ?? 0;
            if (profile is { PortionG: > 0 } && mean > 0) meats.Add((drop, profile.PortionG, mean));
        }
        var originalMass = meats.Sum(m => m.Mass * m.Mean);
        if (originalMass <= 0) return false;
        // Gameplay yield is 10% of the former 30%-of-body-mass budget (3% total).
        // Native body condition, field losses, skills and Butchering workstation efficiency
        // still apply downstream. Hide, bone, fat, blood and bonus prime cuts are separate.
        var factor = entity.Weight * 1000 * .03 / originalMass;
        foreach (var (drop, _, mean) in meats)
        {
            drop["quantity"]!["avg"] = mean * factor;
            if (drop["quantity"]!["var"] is { } variance)
                drop["quantity"]!["var"] = variance.GetValue<double>() * factor;
        }
        copy["seraphMeatYield"] = true;
        var replacement = (JsonObject[])behaviors.Clone();
        replacement[index] = JsonObject.FromJson(copy.ToJsonString());
        entity.Server.BehaviorsAsJsonObj = replacement;
        return true;
    }
}
