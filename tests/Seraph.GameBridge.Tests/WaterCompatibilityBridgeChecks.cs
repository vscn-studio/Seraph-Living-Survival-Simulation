using System.Reflection;
using System.Text.Json;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
internal static class WaterCompatibilityBridgeChecks
{
    internal static void Run(Assembly mod)
    {
        var type = mod.GetType("Seraph.World.WaterMetadata", true)!;
        var liquid = new ItemStack(new Item { Code = new("game:waterportion"), ItemId = 100,
            Attributes = JsonObject.FromJson("{}") }, 25);
        const string key = "vsslssmod:water-v1";
        var water = SurfaceWater.Apply(new() { VolumeL = 1 }, "pond", new());
        type.GetMethod("Write")!.Invoke(null, [liquid, water]);
        var copy = (WaterBatch)type.GetMethod("Read")!.Invoke(null, [liquid, .25d])!;
        if (copy.VolumeL != .25 || copy.EntericHazardPerL != water.EntericHazardPerL || copy.PathogensPerL.Count != 0)
            throw new Exception("Water roundtrip changed dose.");
        var old = JsonSerializer.SerializeToNode(water)!.AsObject(); old.Remove("Format");
        var original = old.ToJsonString(); liquid.Attributes.SetString(key, original);
        foreach (var method in new[] { "Read", "Write" })
        {
            var rejected = false;
            try { type.GetMethod(method)!.Invoke(null, method == "Read" ? [liquid, .25d] : [liquid, water]); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
            if (!rejected || liquid.Attributes.GetString(key) != original) throw new Exception("Old water was accepted or overwritten.");
        }
        var transferType = mod.GetType("Seraph.World.NativeWaterTransfer", true)!;
        var comparable = (ItemStack)mod.GetType("Seraph.World.RecipeMetadataCompatibility", true)!.GetMethod("Comparable",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [liquid, null])!;
        if (comparable.Attributes.HasAttribute(key) || liquid.Attributes.GetString(key) != original)
            throw new Exception("Crafting normalization must ignore unsupported quality metadata on a clone only.");
        const string temperatureStamp = "vsslssmod:water-temperature-updated-at";
        liquid.Attributes.SetDouble(temperatureStamp, 12345);
        comparable = (ItemStack)mod.GetType("Seraph.World.RecipeMetadataCompatibility", true)!.GetMethod("Comparable",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [liquid, null])!;
        if (comparable.Attributes.HasAttribute(key) || comparable.Attributes.HasAttribute(temperatureStamp)
            || !liquid.Attributes.HasAttribute(temperatureStamp))
            throw new Exception("Crafting comparison retained SLSS water metadata or mutated the stored water.");
        ICoreAPI api = null!;
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (method, args) => method.Name switch
        {
            "get_Api" => api,
            "GetItem" when ((AssetLocation)args![0]!).Equals(liquid.Collectible.Code) => liquid.Collectible,
            _ => throw new Exception("Unexpected crafting match world call: " + method.Name)
        });
        api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (method, _) => method.Name switch
        {
            "get_World" => world,
            _ => null
        });
        typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(liquid.Collectible, api);
        var currentWater = liquid.Clone();
        var incomingWater = liquid.Clone();
        currentWater.Attributes.RemoveAttribute(key);
        incomingWater.Attributes.RemoveAttribute(key);
        type.GetMethod("Write")!.Invoke(null, [currentWater, water with { TemperatureC = 20 }]);
        type.GetMethod("Write")!.Invoke(null, [incomingWater, water with { TemperatureC = 12 }]);
        currentWater.Attributes.SetDouble(temperatureStamp, 20000);
        incomingWater.Attributes.SetDouble(temperatureStamp, 30000);
        currentWater.Attributes.SetFloat("temperature", 4);
        incomingWater.Attributes.SetFloat("temperature", 31);
        var identityMatches = (bool)transferType.GetMethod("WaterIdentityMatches",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [world, currentWater, incomingWater])!;
        if (!identityMatches || !currentWater.Attributes.HasAttribute(temperatureStamp)
            || !incomingWater.Attributes.HasAttribute(temperatureStamp))
            throw new Exception("Different stored water temperatures prevented a later pour or were mutated.");
        incomingWater.Attributes.SetString("thirdparty:liquid-identity", "different");
        identityMatches = (bool)transferType.GetMethod("WaterIdentityMatches",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [world, currentWater, incomingWater])!;
        if (identityMatches)
            throw new Exception("Water merge ignored an unrelated third-party liquid attribute.");
        var mergeQuantity = transferType.GetMethod("WaterStackMergableQuantity",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        currentWater.StackSize = 25;
        incomingWater.Attributes.RemoveAttribute("thirdparty:liquid-identity");
        incomingWater.StackSize = 7;
        var mergeArgs = new object?[] { currentWater, incomingWater, EnumMergePriority.DirectMerge, 0 };
        mergeQuantity.Invoke(null, mergeArgs);
        if ((int)mergeArgs[3]! != 7)
            throw new Exception("Different native water temperatures still blocked item stack merging.");
        incomingWater.Attributes.SetString("thirdparty:liquid-identity", "different");
        mergeArgs[3] = 0;
        mergeQuantity.Invoke(null, mergeArgs);
        if ((int)mergeArgs[3]! != 0)
            throw new Exception("Water item merging ignored an unrelated third-party attribute.");
        var required = JsonObject.FromJson("{\"type\":\"item\",\"code\":\"game:waterportion\"}")
            .AsObject<JsonItemStack>()!;
        if (!required.Resolve(world, "water crafting regression") || !required.Matches(world, comparable))
            throw new Exception("Water with SLSS quality/temperature metadata still fails native recipe matching.");
        var coolingType = mod.GetType("Seraph.World.StoredWaterCooling", true)!;
        var advance = coolingType.GetMethod("Advance", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Temperature(double water, double ambient, double seconds)
            => (double)advance.Invoke(null, [water, ambient, seconds])!;
        var afterTenSeconds = Temperature(22, 0, 10);
        if (afterTenSeconds < 21.9 || afterTenSeconds > 22)
            throw new Exception("Stored water still snaps toward 0 C within seconds.");
        if (Math.Abs(Temperature(22, 0, 12 * 3600) - 11) > .001)
            throw new Exception("Stored water cooling no longer follows its 12-hour ambient half-life.");
        if (Temperature(4, 20, 12 * 3600) != 12)
            throw new Exception("Stored water does not warm toward its ambient temperature.");
        Console.WriteLine("PASS release water roundtrip and old metadata read/write rejection");
    }
}
