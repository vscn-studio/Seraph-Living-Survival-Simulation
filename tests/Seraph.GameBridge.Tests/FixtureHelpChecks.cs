using System.Reflection;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class FixtureHelpChecks
{
    internal static void Run()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var bucket = new BlockBucket { Code = new("game:woodbucket"), BlockId = 2 };
        var soap = new Item { Code = new("vsslssmod:soap"), ItemId = 3 };
        var well = new BlockEntityWellHead();
        typeof(BlockEntityWellHead).GetField("wellComplete", hidden)!.SetValue(well, true);
        var accessor = (IBlockAccessor)RoutingProxy.Make(typeof(IBlockAccessor), (m, _) => m.Name == "GetBlockEntity" ? well : throw new Exception(m.Name));
        var scans = 0;
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, _) => m.Name switch {
            "get_Blocks" => Blocks(), "get_Items" => new Item[] { soap }, "get_BlockAccessor" => accessor,
            _ => throw new Exception(m.Name)
        });
        Block[] Blocks() { scans++; return [bucket]; }
        var selection = new BlockSelection { Position = new BlockPos(0, 0, 0) };
        var bathBlock = new BlockBathtub();
        var bath = bathBlock.GetPlacedBlockInteractionHelp(world, selection, null!);
        if (bath.Length != 3 || bath.Any(h => h.MouseButton != EnumMouseButton.Right)
            || bath[0].Itemstacks.Single().Block != bucket || bath[2].Itemstacks.Single().Item != soap)
            throw new Exception("Bath help lost its native mouse/soap/container slides.");
        var wellBlock = new BlockWellHead();
        var lower = wellBlock.GetPlacedBlockInteractionHelp(world, selection, null!);
        if (lower.Single().Itemstacks.Single().Block != bucket) throw new Exception("Well help omits the accepted empty wooden bucket.");
        typeof(BlockEntityWellHead).GetField("bucketInWell", hidden)!.SetValue(well, true);
        var raise = wellBlock.GetPlacedBlockInteractionHelp(world, selection, null!);
        if (raise.Single().HotKeyCode != "shift") throw new Exception("Well raise help omits the held Shift modifier.");
        typeof(BlockEntityWellHead).GetField("bucketRaised", hidden)!.SetValue(well, true);
        var take = wellBlock.GetPlacedBlockInteractionHelp(world, selection, null!);
        for (var i = 0; i < 1000; i++)
            if (!ReferenceEquals(bath, bathBlock.GetPlacedBlockInteractionHelp(world, selection, null!))
                || !ReferenceEquals(take, wellBlock.GetPlacedBlockInteractionHelp(world, selection, null!)))
                throw new Exception("Fixture hover rebuilt its native help arrays.");
        if (scans != 2 || take.Single().HotKeyCode is not null) throw new Exception("Fixture hover rescanned all blocks or changed take controls.");
        Console.WriteLine("PASS cached native bath/well mouse, Shift and item-slide help; state transitions and 1000 refreshes without rescanning");
    }
}
