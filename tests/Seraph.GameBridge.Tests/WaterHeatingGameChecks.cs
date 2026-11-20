using System.Reflection;
using System.Text.Json;
using Seraph;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Server;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class WaterHeatingGameChecks
{
    internal static void Run()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var mod = new SeraphModSystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? mod : null);
        var totalHours = 0d;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? totalHours : null);
        var item = new Item { ItemId = 100, Code = new("game:waterportion"),
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        ICoreServerAPI api = null!;
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_Api" => api, "get_Calendar" => calendar, "GetItem" => item,
            _ => throw new Exception("Unexpected heating world call: " + m.Name)
        });
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server,
            _ => throw new Exception("Unexpected heating API call: " + m.Name)
        });
        var assembly = typeof(SeraphModSystem).Assembly;
        typeof(SeraphModSystem).GetField("server", hidden)!.SetValue(mod,
            Activator.CreateInstance(assembly.GetType("Seraph.Server.ServerCoordinator", true)!, [api]));
        var vessel = new BlockBucket { BlockId = 50, Code = new("game:bucket-wood") };
        typeof(CollectibleObject).GetField("api", hidden)!.SetValue(vessel, api);
        typeof(CollectibleObject).GetField("api", hidden)!.SetValue(item, api);
        var stack = new ItemStack(vessel);
        var liquid = new ItemStack(item, 200);
        const string key = "vsslssmod:water-v1";
        WaterBatch Read(ItemStack content, double litres) => JsonSerializer.Deserialize<WaterBatch>(content.Attributes.GetString(key))! with { VolumeL = litres };
        liquid.Attributes.SetString(key, JsonSerializer.Serialize(new WaterBatch { EntericHazardPerL = 1, SaltGramsPerL = 10 }));
        var boilingItem = new Item { ItemId = 101, Code = new("game:boilingwaterportion"),
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        typeof(CollectibleObject).GetField("api", hidden)!.SetValue(boilingItem, api);
        var boiling = new ItemStack(boilingItem, 100);
        Require(WaterQuality.IsWater(boiling), "Native boiling water was not recognized as water.");
        var boilingBatch = WaterQuality.ReadLiquidAt(world, boiling, 20, new SeraphServerConfig());
        Require(boilingBatch is { TemperatureC: 100 }, "Unstamped boiling water did not retain its native 100 C temperature.");
        vessel.SetContent(stack, liquid);
        var firepit = new BlockEntityFirepit { Api = api, furnaceTemperature = 600 };
        var inventory = firepit.Inventory;
        inventory.Api = api;
        var input = new DummySlot(stack);
        inventory[1] = input;
        typeof(BlockEntityFirepit).GetField("inventory", hidden)!.SetValue(firepit, inventory);
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        using (var bridge = (IDisposable)Activator.CreateInstance(assembly.GetType("Seraph.World.NativeWaterHeating", true)!)!)
        {
            Require(firepit.canHeatInput(), "Water vessel cannot enter native heating path.");
            vessel.SetTemperature(world, stack, 99);
            Require(Read(vessel.GetContent(stack)!, 2).EntericHazardPerL == 1, "99 C sterilized water.");
            totalHours = 1;
            var cooled = WaterQuality.ReadLiquidAt(world, vessel.GetContent(stack), 0, new SeraphServerConfig());
            Require(cooled is { TemperatureC: > 93 and < 94 }, "Stored vessel water did not cool toward ambient gradually.");
            vessel.SetTemperature(world, stack, 100);
            Require(Read(vessel.GetContent(stack)!, 2).EntericHazardPerL == 0, "Native temperature setter did not boil vessel water.");
            firepit.heatInput(60);
            var remaining = vessel.GetContent(stack)!;
            Require(remaining.StackSize is > 0 and < 200, "Native firepit did not evaporate actual liquid units.");
            var batch = Read(remaining, remaining.StackSize / 100d);
            Require(batch.EntericHazardPerL == 0 && Math.Abs(batch.SaltGramsPerL * batch.VolumeL - 20) < 1e-8,
                "Firepit quality writeback lost salt or sterilization.");
            Require(firepit.InputStackTemp == 100, "Native firepit temperature display is stale.");
            firepit.furnaceTemperature = 20;
            firepit.heatInput(10);
            Require(firepit.InputStackTemp is > 20 and < 100 && vessel.GetContent(stack)!.StackSize == remaining.StackSize,
                "Extinguished firepit did not cool water or evaporated cold water.");
            firepit.furnaceTemperature = 600;
            for (var i = 0; i < 5; i++) firepit.heatInput(60);
            Require(vessel.GetContent(stack) is null && ReferenceEquals(input.Itemstack, stack), "Evaporation lost vessel or left phantom water.");
            Require(Math.Abs(stack.Attributes.GetDouble("vsslssmod:salt-residue-g") - 20) < 1e-8,
                "Dry boiling deleted sea salt instead of retaining vessel residue.");
        }
        var residue = new SaltResidueBehavior(firepit) { Grams = 37.5 };
        var tree = new TreeAttribute(); residue.ToTreeAttributes(tree);
        var restored = new SaltResidueBehavior(firepit); restored.FromTreeAttributes(tree, world);
        Require(restored.Grams == 37.5, "Boiler residue did not survive serialization.");
        var dropped = new ItemStack(vessel); dropped.Attributes.SetDouble("vsslssmod:salt-residue-g", 37.5);
        restored.OnBlockPlaced(dropped);
        Require(restored.Grams == 37.5, "Boiler placement lost fractional salt.");
        using (var distillation = (IDisposable)Activator.CreateInstance(assembly.GetType("Seraph.World.NativeWaterDistillation", true)!, nonPublic: true)!)
            Console.WriteLine("PASS native water distillation hooks install on actual game methods");
        Console.WriteLine("PASS native firepit heating, vessel/liquid boiling, exact evaporation units and empty-vessel preservation");
    }
}
