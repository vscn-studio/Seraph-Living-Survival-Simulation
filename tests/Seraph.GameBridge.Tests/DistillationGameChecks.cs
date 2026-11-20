using System.Reflection;
using System.Text.Json;
using Seraph;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class DistillationGameChecks
{
    internal static void Run()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var mod = new SeraphModSystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? mod : null);
        var item = new Item { ItemId = 100, Code = new("game:waterportion"), Attributes = JsonObject.FromJson(
            "{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        var vessel = new TestBucket { BlockId = 50, Code = new("game:bucket-wood") };
        var boiler = new TestBoiler();
        var salt = new SaltResidueBehavior(boiler);
        boiler.Behaviors.Add(salt);
        var blocks = Proxy<IBlockAccessor>((m, _) => m.Name == "GetBlockEntity" ? boiler : null);
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? 0d : null);
        ICoreServerAPI api = null!;
        var random = new Random(12);
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_Api" => api, "GetItem" => item, "get_BlockAccessor" => blocks,
            "get_Rand" => random, "get_Blocks" => new Block[] { vessel }, "get_ElapsedMilliseconds" => 1000L,
            "get_Calendar" => calendar,
            _ => null
        });
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server, _ => null
        });
        var assembly = typeof(SeraphModSystem).Assembly;
        typeof(SeraphModSystem).GetField("server", hidden)!.SetValue(mod,
            Activator.CreateInstance(assembly.GetType("Seraph.Server.ServerCoordinator", true)!, [api]));
        foreach (var obj in new CollectibleObject[] { item, vessel })
            typeof(CollectibleObject).GetField("api", hidden)!.SetValue(obj, api);
        boiler.Api = api;
        boiler.Inventory.Api = api; boiler.Inventory.Pos = new BlockPos(0, 1, 0);
        var condenser = new TestCondenser { Api = api };
        condenser.Inventory.Api = api;
        boiler.Inventory[0] = new QuietSlot(boiler.Inventory);
        condenser.Inventory[0] = new QuietSlot(condenser.Inventory);
        condenser.Inventory[1] = new QuietSlot(condenser.Inventory);
        var receiver = new ItemStack(vessel);
        condenser.Inventory[1].Itemstack = receiver;
        var source = new ItemStack(item, 100);
        source.Attributes.SetString("vsslssmod:water-v1", JsonSerializer.Serialize(new WaterBatch {
            VolumeL = 1, SaltGramsPerL = 35, EntericHazardPerL = 1, ChemicalHazardPerL = .3 }));
        item.SetTemperature(world, source, 100);
        boiler.Inventory[0].Itemstack = source;
        var transferType = assembly.GetType("Seraph.World.NativeWaterTransfer", true)!;
        using var transfer = (IDisposable)Activator.CreateInstance(transferType, [api])!;
        transferType.GetMethod("RegisterBlocks")!.Invoke(transfer, null);
        using var distillation = (IDisposable)Activator.CreateInstance(assembly.GetType("Seraph.World.NativeWaterDistillation", true)!, true)!;
        void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
        Require(condenser.ReceiveDistillate(boiler.Inventory[0], new()), "Empty receiver rejected boiling water.");
        Require(source.StackSize == 99 && vessel.GetContent(receiver)!.StackSize == 1, "Native transfer did not charge exactly accepted water.");
        var output = JsonSerializer.Deserialize<WaterBatch>(vessel.GetContent(receiver)!.Attributes.GetString("vsslssmod:water-v1"))!;
        Require(output.Source == "distilled" && output.SaltGramsPerL == 0 && output.ChemicalHazardPerL == 0, "Condensate retained contamination.");
        item.SetTemperature(world, source, 99);
        Require(!condenser.ReceiveDistillate(boiler.Inventory[0], new()) && source.StackSize == 99, "Cold water was consumed.");
        item.SetTemperature(world, source, 100);
        vessel.SetContent(receiver, new ItemStack(item, (int)(vessel.CapacityLitres * 100)));
        Require(!condenser.ReceiveDistillate(boiler.Inventory[0], new()) && source.StackSize == 99 && salt.Grams == 0, "Full receiver consumed water or created salt.");
        receiver = condenser.Inventory[1].Itemstack!;
        vessel.SetContent(receiver, null!);
        // Cooling water prevents stochastic air-cooling misses; retrying must conserve each accepted unit.
        var accepted = 1;
        for (var attempt = 0; !boiler.Inventory[0].Empty && attempt < 200; attempt++)
        {
            condenser.Inventory[0].Itemstack = new ItemStack(item, 1000);
            if (condenser.ReceiveDistillate(boiler.Inventory[0], new())) accepted++;
        }
        Require(accepted == 100 && Math.Abs(salt.Grams - 35) < 1e-7, "Full native distillation did not conserve salt and water.");
        Console.WriteLine("PASS native condenser accepted/rejected transfers, cold/full receivers and salt conservation");
    }
    private sealed class TestBoiler : BlockEntityBoiler { public override void MarkDirty(bool redraw = false, IPlayer? skip = null) { } }
    private sealed class TestCondenser : BlockEntityCondenser { public override void MarkDirty(bool redraw = false, IPlayer? skip = null) { } }
    private sealed class TestBucket : BlockBucket { public override float CapacityLitres => 10; }
    private sealed class QuietSlot(InventoryBase inv) : ItemSlot(inv) { public override void MarkDirty() { } }
}
