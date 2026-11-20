using System.Reflection;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class AshBridgeChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        var ash = new Item { Code = new("vsslssmod:ash"), ItemId = 202, MaxStackSize = 64 };
        var manure = new ItemManure { Code = new("vsslssmod:manure"), ItemId = 201, MaxStackSize = 64 };
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m,a) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "GetItem" => a![0] is int id ? id == 201 ? manure : ash : ash,
            "get_ElapsedMilliseconds" => 0L, "get_BlockAccessor" => RoutingProxy.Make(typeof(IBlockAccessor), (_,_) => null), _ => null });
        var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m,_) => m.Name switch {
            "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null });
        typeof(CollectibleObject).GetField("api", hidden)!.SetValue(manure, api);
        typeof(CollectibleObject).GetField("api", hidden)!.SetValue(ash, api);
        var patchType = mod.GetType("Seraph.World.NativeAshGeneration", true)!;
        using var patch = (IDisposable)Activator.CreateInstance(patchType, nonPublic: true)!;
        var inv = new InventorySmelting("smelting-test", null!);
        Check(inv.Count == 8 && inv[7] is ItemSlotOutput && inv.Slots.Length == 4 && ReferenceEquals(inv.Slots[0],inv[3]), "Ash changed original cooking slots.");
        Check(!inv[7].CanHold(new DummySlot(new ItemStack(manure))), "Ash output accepted insertion.");
        var legacy = new TreeAttribute(); legacy.SetInt("qslots", 7);
        inv.FromTreeAttributes(legacy);
        Check(inv.Count == 8 && inv[7].Empty, "Legacy seven-slot migration failed.");
        inv[7].Itemstack = new ItemStack(ash, 37);
        var saved = new TreeAttribute(); inv.ToTreeAttributes(saved);
        var restored = new InventorySmelting("smelting-test2", null!); restored.Api = api; restored.FromTreeAttributes(saved);
        Check(restored.Count == 8 && restored[7].StackSize == 37 && restored[7] is ItemSlotOutput, "Ash persistence failed.");
        var firepit = new BlockEntityFirepit { Api = api, Pos = new BlockPos(0,0,0,0) };
        firepit.Inventory.Api = api;
        var fuel = typeof(BlockEntityFirepit).GetField("fuelBurnTime", hidden)!;
        var complete = patchType.GetMethod("AfterFuelDecrement", hidden)!;
        fuel.SetValue(firepit, 1f); complete.Invoke(null, [firepit]);
        Check(firepit.Inventory[7].Empty, "Unfinished fuel yielded ash.");
        // MarkDirty needs a world accessor. Test accumulation with native inventory dirtiness and no BE network.
        firepit.Api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m,_) => m.Name switch {
            "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null });
        fuel.SetValue(firepit, 0f);
        for (var i=0;i<70;i++) complete.Invoke(null, [firepit]);
        Check(firepit.Inventory[7].StackSize == 64, "Ash cap failed.");
        firepit.Inventory[7].TakeOut(10);
        fuel.SetValue(firepit, 1f); complete.Invoke(null, [firepit]);
        Check(firepit.Inventory[7].StackSize == 54, "Full ash accumulated a deferred backlog.");
        Console.WriteLine("PASS ash patch installation, native slots, output-only, legacy migration, persistence, completed-fuel hook and cap");
    }
}
