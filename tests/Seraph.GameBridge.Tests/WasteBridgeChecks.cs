using System.Reflection;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class WasteBridgeChecks
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
        var sink = new DummySlot(new ItemStack(manure, 63));
        var source = new DummySlot(new ItemStack(manure, 3));
        source.Itemstack!.Attributes.SetDouble("vsslssmod:manureMassG", 1000);
        source.Itemstack!.Attributes.SetString("vsslssmod:pathogens-v1", "{\"shigella\":0.5}");
        var op = new ItemStackMergeOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, 3) {
            SinkSlot = sink, SourceSlot = source };
        manure.TryMergeStacks(op);
        Check(op.MovedQuantity == 1 && sink.StackSize == 64 && source.StackSize == 2, "Manure partial native merge failed.");
        Check(Math.Abs(sink.Itemstack!.Attributes.GetDouble("vsslssmod:manureMassG") * 64 - 7300) < 1e-8,
            "Manure merge lost historical mass.");
        var loads = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,double>>(sink.Itemstack!.Attributes.GetString("vsslssmod:pathogens-v1"))!;
        Check(Math.Abs(loads["shigella"] * 7300 - 500) < 1e-8, "Manure merge lost mass-weighted pathogens.");
        var split = sink.TakeOut(2);
        Check(Math.Abs(split.Attributes.GetDouble("vsslssmod:manureMassG") * 2 + sink.Itemstack!.Attributes.GetDouble("vsslssmod:manureMassG") * 62 - 7300) < 1e-8, "Split lost mass.");
        source.Itemstack!.Attributes.SetString("foreign:owner", "other");
        Check(manure.GetMergableQuantity(sink.Itemstack, source.Itemstack, EnumMergePriority.DirectMerge) == 0, "Foreign attribute barrier removed.");
        Console.WriteLine("PASS native manure partial merge, split, legacy mass, pathogen conservation and foreign barriers");

    }
}
