using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

internal static class NativeStatChecks
{
    internal static void Run(Assembly mod)
    {
        var type = mod.GetType("Seraph.Server.NativeStatInputs", true)!;
        object? Call(string name, params object?[] args) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
        var player = new EntityPlayer();
        player.Stats.Register("walkspeed");
        player.Stats.Set("walkspeed", "sitWalkingBonus", .1f);
        Call("SetOwned", player, "walkspeed", "vsslssmod:function", -.2f);
        if (Math.Abs((double)Call("Multiplier", player, "walkspeed")! - .9) > 1e-6) throw new Exception("Native additive walking modifiers were lost.");
        Call("SetOwned", player, "walkspeed", "vsslssmod:function", .8f);
        foreach (var blend in Enum.GetValues<EnumStatBlendType>())
        {
            player.Stats["walkspeed"].BlendType = blend;
            player.Stats["walkspeed"].ValuesByKey["sitWalkingBonus"].Weight = .4f;
            var expected = player.Stats.GetBlended("walkspeed");
            if ((double)Call("Multiplier", player, "walkspeed")! != expected) throw new Exception("Native blend order/weight changed.");
        }
        Call("SetOwned", player, "walkspeed", "vsslssmod:function", -.2f);
        var changes = 0;
        player.WatchedAttributes.RegisterModifiedListener("stats", () => changes++);
        Call("SetOwned", player, "walkspeed", "vsslssmod:function", -.2f);
        Call("SetOwned", player, "hungerrate", "absent", null);
        if (changes != 0) throw new Exception("Unchanged stats rewrote watched attributes.");
        Call("SetOwned", player, "walkspeed", "vsslssmod:function", null);
        if (!player.Stats["walkspeed"].ValuesByKey.ContainsKey("sitWalkingBonus")) throw new Exception("External modifier removed.");
        if ((string)Call("SourceLabel", "arbitrary:untranslated-source")! != "native-source-other") throw new Exception("Unknown modifier has no readable fallback.");
        var health = new EntityBehaviorHealth(player);
        player.World = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor),
            (method, _) => method.Name == "get_Side" ? EnumAppSide.Server : throw new Exception(method.Name));
        typeof(Vintagestory.API.Common.Entities.Entity).GetProperty("Properties")!.SetValue(player,
            new Vintagestory.API.Common.Entities.EntityProperties { Server = new EntityServerProperties([], new()) });
        player.AddBehavior(health);
        typeof(EntityBehaviorHealth).GetField("maxHealthModifiers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(health, new Dictionary<string, float> { ["nutrientHealthMod"] = 15, ["other-mod"] = 7 });
        player.Stats.Set("maxhealthExtraPoints", "sitHardyHealthBonus", 10);
        if ((double)Call("ExternalHealth", player)! != 17) throw new Exception("External absolute health included native food-group bonus or lost hardy bonus.");
        player.Stats.Set("maxhealthExtraPoints", "curse", -20);
        if ((double)Call("ExternalHealth", player)! != -3) throw new Exception("Negative external health was treated as a multiplier.");
        player.Stats.Set("hungerrate", "sitHungerBonus", -.15f);
        if (Math.Abs((double)Call("PhysiologyMultiplier", player, "hungerrate")! - .85) > 1e-6) throw new Exception("Native hunger reduction not read.");
        player.Stats.Set("hungerrate", "invalid-mod", float.NaN);
        if ((double)Call("PhysiologyMultiplier", player, "hungerrate")! != 1) throw new Exception("Invalid optional input faulted physiology.");
        Console.WriteLine("PASS native stat blending, external ownership, fallback labels and unchanged-write suppression");
    }
}
