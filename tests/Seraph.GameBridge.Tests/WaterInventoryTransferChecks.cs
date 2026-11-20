using System.Reflection;
using Seraph;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class WaterInventoryTransferChecks
{
    internal static void Run()
    {
        foreach (var side in new[] { EnumAppSide.Server, EnumAppSide.Client }) Run(side);
        Console.WriteLine("PASS actual cooking-slot clicks and stack merging on client/server: repeated draws, cooling, evaporation, quality conservation, limits and unknown metadata");
    }

    private static void Run(EnumAppSide side)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var mod = new SeraphModSystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? mod : null);
        var hours = 0d;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? hours : null);
        var water = new Item { ItemId = 100, Code = new("game:waterportion"), MaxStackSize = 1000,
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        var bowl = new TestBowl { BlockId = 20, Code = new("game:bowl") };
        ICoreAPI api = null!;
        var world = Proxy<IWorldAccessor>((m, _) => m.Name switch {
            "get_Api" => api, "get_Side" => side, "get_Calendar" => calendar,
            "GetItem" => water, "get_Blocks" => new Block[] { bowl }, _ => null
        });
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Side" => side, "get_ModLoader" => loader, _ => null
        });
        typeof(SeraphModSystem).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mod, new Seraph.Server.ServerCoordinator((ICoreServerAPI)api));
        foreach (var obj in new CollectibleObject[] { water, bowl })
            typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, api);
        var inventory = new InventoryGeneric(2, "water-test", "1", null!) { Api = api };
        var pot = new QuietCookingSlot(inventory);
        var cursor = new QuietSlot(inventory) { Itemstack = new ItemStack(bowl) };
        inventory[0] = pot; inventory[1] = cursor;
        using var transfer = new NativeWaterTransfer(api);
        transfer.RegisterBlocks();
        using var heating = new NativeWaterHeating();
        ItemStack Batch(int count, double temp, double hazard, double salt = 0)
        {
            var stack = new ItemStack(water, count);
            WaterMetadata.WriteAt(world, stack, new WaterBatch { TemperatureC = temp,
                EntericHazardPerL = hazard, SaltGramsPerL = salt, Turbidity = hazard / 10 });
            return stack;
        }
        WaterBatch Read(ItemStack? stack) => WaterQuality.ReadLiquidAt(world, stack, 15, new())!;
        int Click(EnumMouseButton button, bool shift = false)
        {
            var op = new ItemStackMoveOperation(world, button, shift ? EnumModifierKey.SHIFT : 0, EnumMergePriority.DirectMerge);
            pot.ActivateSlot(cursor, ref op);
            return op.MovedQuantity;
        }
        pot.Itemstack = Batch(300, 95, 2, 1);
        pot.Itemstack.Attributes.SetDouble(NativeWaterHeating.RemainderKey, .75);
        Require(Click(EnumMouseButton.Right) == 25 && pot.StackSize == 275, "First bowl draw failed.");
        hours = 1;
        bowl.SetContent(cursor.Itemstack, Batch(25, 10, 0));
        var incoming = Read(pot.Itemstack);
        Require(Click(EnumMouseButton.Right) == 25 && pot.StackSize == 250, "Second draw after cooling failed.");
        var mixed = Read(bowl.GetContent(cursor.Itemstack)!);
        Near(mixed.TemperatureC, (10 + incoming.TemperatureC) / 2, "Draw copied bowl temperature instead of mixing pot water.");
        Near(mixed.EntericHazardPerL, 1, "Draw copied bowl quality instead of pot quality.");
        Near(mixed.SaltGramsPerL, .5, "Draw lost salt.");
        Require(Click(EnumMouseButton.Left) == 25 && pot.StackSize == 275, "Pouring back into heated water failed.");
        Near(Read(pot.Itemstack).EntericHazardPerL * 2.75 + Read(bowl.GetContent(cursor.Itemstack)!).EntericHazardPerL * .25,
            5.5, "Pour-back changed total contamination.");
        Require(Click(EnumMouseButton.Right, true) == 75 && bowl.GetContent(cursor.Itemstack)!.StackSize == 100,
            "Shift draw did not respect bowl capacity.");
        var before = pot.StackSize;
        Require(Click(EnumMouseButton.Right) == 0 && pot.StackSize == before, "Full bowl consumed water.");
        bowl.SetContent(cursor.Itemstack, Batch(25, 30, 1));
        var blocked = bowl.GetContent(cursor.Itemstack)!;
        blocked.Attributes.SetString("thirdparty:identity", "other");
        bowl.SetContent(cursor.Itemstack, blocked);
        Require(Click(EnumMouseButton.Right) == 0 && Click(EnumMouseButton.Left) == 0 && pot.StackSize == before,
            "Unknown liquid identity was ignored.");
        bowl.SetContent(cursor.Itemstack, null!);
        cursor.Itemstack.StackSize = 3;
        pot.Itemstack = Batch(77, 50, 1);
        Require(Click(EnumMouseButton.Right) == 75 && pot.StackSize == 2
            && bowl.GetContent(cursor.Itemstack)!.StackSize == 25, "Stacked bowls duplicated or lost water.");
        Require(Click(EnumMouseButton.Right) == 0 && pot.StackSize == 2, "Uneven remainder duplicated water across bowls.");
        Require(Click(EnumMouseButton.Left) == 75 && pot.StackSize == 77 && bowl.GetContent(cursor.Itemstack)! is null,
            "Stacked bowls did not pour back the actual amount.");
        pot.Itemstack = Batch(590, 20, 1);
        bowl.SetContent(cursor.Itemstack, Batch(25, 60, 2));
        Require(Click(EnumMouseButton.Left) == 9 && pot.StackSize == 599
            && bowl.GetContent(cursor.Itemstack)!.StackSize == 22, "Pot capacity or stack divisibility was violated.");

        cursor.Itemstack.StackSize = 1;
        bowl.SetContent(cursor.Itemstack, Batch(25, 5, 0));
        var refill = Batch(200, 95, 2);
        refill.Attributes.SetDouble(NativeWaterHeating.RemainderKey, .3);
        Require(bowl.TryPutLiquid(cursor.Itemstack, refill, .25f) == 25,
            "Held-container refill failed on " + side);
        Near(Read(bowl.GetContent(cursor.Itemstack)).TemperatureC, 50, "Held refill lost heat.");
        Require(bowl.TryPutLiquid(cursor.Itemstack, refill, 10) == 50
            && bowl.GetContent(cursor.Itemstack)!.StackSize == 100, "Held refill exceeded capacity.");

        // Invoke actual patched merging, not the quantity callback in isolation.
        var sink = new QuietSlot(inventory) { Itemstack = Batch(40, 5, 0), MaxSlotStackSize = 50 };
        var source = new QuietSlot(inventory) { Itemstack = Batch(40, 95, 2, 1) };
        source.Itemstack.Attributes.SetDouble(NativeWaterHeating.RemainderKey, .5);
        var merge = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 40).ToMergeOperation(sink, source);
        water.TryMergeStacks(merge);
        Require(merge.MovedQuantity == 10 && sink.StackSize == 50 && source.StackSize == 30,
            "Real stack merge still refused temperature/evaporation differences or exceeded slot space.");
        Near(Read(sink.Itemstack).TemperatureC, 23, "Stack merge did not conserve heat.");
        Near(Read(sink.Itemstack).EntericHazardPerL, .4, "Stack merge did not conserve contamination.");
        sink.MaxSlotStackSize = 100;
        source.Itemstack.Attributes.SetString("thirdparty:identity", "other");
        water.TryMergeStacks(merge);
        Require(merge.MovedQuantity == 0 && source.StackSize == 30, "Unknown metadata allowed a real merge.");
        source.Itemstack.Attributes.RemoveAttribute("thirdparty:identity");
        source.Itemstack.Attributes.SetString(WaterMetadata.Key, "broken");
        water.TryMergeStacks(merge);
        Require(merge.MovedQuantity == 0 && source.StackSize == 30, "Malformed metadata allowed a real merge.");
    }
    private static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    private static void Near(double actual, double expected, string why) => Require(Math.Abs(actual - expected) < 1e-6, why);
    private sealed class TestBowl : BlockLiquidContainerTopOpened
    {
        public override float CapacityLitres => 1;
        public override float TransferSizeLitres => .25f;
    }
    private sealed class QuietSlot(InventoryBase inventory) : ItemSlot(inventory) { public override void MarkDirty() { } }
    private sealed class QuietCookingSlot(InventoryBase inventory) : ItemSlotWatertight(inventory, 6)
    { public override void MarkDirty() { } }
}
