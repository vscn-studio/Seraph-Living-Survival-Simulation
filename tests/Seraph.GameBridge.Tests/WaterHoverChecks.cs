using System.Reflection;
using Seraph;
using Seraph.Core.Framework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class WaterHoverChecks
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run()
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var air = new Block { Code = new("game:air"), BlockId = 0 };
        var water = new BlockWater { Code = new("game:water-still-7"), BlockId = 1, LiquidCode = "water" };
        var stone = new Block { Code = new("game:rock-granite"), BlockId = 2,
            BlockMaterial = EnumBlockMaterial.Stone, CreativeInventoryTabs = ["terrain"] };
        var plant = new Block { Code = new("game:waterlily"), BlockId = 3, BlockMaterial = EnumBlockMaterial.Plant };
        var bucket = new HoverBucket { Code = new("game:bucket-wood"), BlockId = 4 };
        var barrel = new BlockBarrel { Code = new("game:barrel-oak"), BlockId = 5 };
        var fluids = new Dictionary<BlockPos, Block>();
        var solids = new Dictionary<BlockPos, Block>();
        var containers = new Dictionary<BlockPos, BlockEntity>();
        var accessor = Proxy<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetBlock" => ((int)a![1]! == BlockLayersAccess.Fluid ? fluids : solids).GetValueOrDefault((BlockPos)a[0]!, air),
            "GetBlockEntity" => containers.GetValueOrDefault((BlockPos)a![0]!),
            "GetClimateAt" or "GetDecors" => null,
            _ => throw new Exception("Unexpected water hover accessor: " + m.Name)
        });
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_HoursPerDay" ? 24f
            : throw new Exception("Unexpected bed calendar: " + m.Name));
        var world = Proxy<IClientWorldAccessor>((m, _) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Blocks" => new Block[] { bucket, barrel },
            "get_Calendar" => calendar,
            _ => throw new Exception("Unexpected water hover world: " + m.Name)
        });
        Action? finalizeLevel = null;
        var events = Proxy<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "add_LevelFinalize") { finalizeLevel += (Action)a![0]!; return null; }
            if (m.Name == "remove_LevelFinalize") { finalizeLevel -= (Action)a![0]!; return null; }
            throw new Exception("Unexpected water hover event: " + m.Name);
        });
        var api = Proxy<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Event" => events,
            _ => throw new Exception("Unexpected water hover API: " + m.Name)
        });
        var entity = new EntityPlayer();
        entity.Pos.SetPos(.5, .5, .5);
        var targetType = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.WaterInteractionTarget", true)!;
        var hover = targetType.GetMethod("FindForHover", Hidden)!;
        var ray = targetType.GetMethod("TryFindWaterAlongRay", Hidden)!;
        var isWaterContainer = targetType.GetMethod("IsWaterContainer", Hidden)!;
        var cornerWater = new BlockPos(1, 0, 0, 0);
        fluids[cornerWater] = water;
        object?[] rayArgs = [world, new Vec3d(.95, .8, .5), new Vec3d(1.4, 1.8, .5), null, null];
        if (!(bool)ray.Invoke(null, rayArgs)! || !cornerWater.Equals(rayArgs[4]))
            throw new Exception("Short corner intersection skipped the fluid cell.");
        var traverse = targetType.GetMethod("RayCells", Hidden)!;
        BlockPos[] Cells(Vec3d from, Vec3d to) => ((IEnumerable<BlockPos>)traverse.Invoke(null, [from, to])!).ToArray();
        var diagonal = Cells(new Vec3d(.5, .5, .5), new Vec3d(2.5, 2.5, 2.5));
        if (diagonal.Length != 3 || diagonal.Any(p => p.X != p.Y || p.Y != p.Z))
            throw new Exception("Corner-only cells obstructed a diagonal ray.");
        var backwards = Cells(new Vec3d(0, .5, .5), new Vec3d(-2, .5, .5));
        if (!backwards.Select(p => p.X).SequenceEqual(new[] { 0, -1, -2 }))
            throw new Exception("Negative grid boundary or endpoint traversal is incorrect.");
        if (Cells(new Vec3d(.5, .5, .5), new Vec3d(.5, .5, .5)).Length != 1
            || Cells(new Vec3d(double.NaN, 0, 0), new Vec3d()).Length != 0)
            throw new Exception("Zero-length or invalid ray did not terminate correctly.");
        fluids.Clear();
        var pos = new BlockPos(0, 0, 2, 0);
        var selection = new BlockSelection { Position = pos, HitPosition = new Vec3d(.5, .5, .5), Face = BlockFacing.NORTH };
        BlockPos? Find() => (BlockPos?)hover.Invoke(null, [world, entity, selection, 6d]);
        var interaction = targetType.GetMethod("Find", Hidden)!;
        BlockPos? ActionTarget() => (BlockPos?)interaction.Invoke(null, [world, entity, 6d]);
        void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        solids[pos] = plant; fluids[pos] = water;
        Require(Find() is null, "Underwater plant was replaced by a water hover.");
        solids[pos] = bucket;
        Require(Find() is null, "Container sharing a fluid cell was replaced by still water.");
        fluids.Clear(); fluids[pos.AddCopy(1, 0, 0)] = water;
        Require(Find() is null, "Oblique container selection picked neighboring water.");
        solids[pos] = stone;
        Require(Find() is null, "Off-ray water was selected beside terrain.");
        // The actual action ray must stop at the bank, not continue into water behind it.
        entity.Pos.Yaw = MathF.PI; entity.Pos.Pitch = 0;
        fluids.Clear(); fluids[pos.AddCopy(0, 0, 1)] = water;
        Require(ActionTarget() is null, "Shift + right-click targeted water through the bank.");
        fluids[pos] = water;
        Require(Find() is null && ActionTarget() is null, "A solid bank sharing a fluid cell became a water target.");
        fluids.Clear(); fluids[pos.AddCopy(0, 0, -1)] = water;
        Require(ActionTarget()?.Equals(pos.AddCopy(0, 0, -1)) == true, "Directly visible water in front of the bank was rejected.");
        fluids.Clear(); solids[pos] = bucket; fluids[pos.AddCopy(0, 0, 1)] = water;
        Require(ActionTarget()?.Equals(pos) == true, "Water behind a container stole the container interaction.");
        solids[pos] = stone;
        fluids.Clear(); fluids[pos.AddCopy(0, 0, 1)] = water;
        Require(Find() is null, "Hover ray continued through the selected solid into water behind it.");
        fluids.Clear(); fluids[pos.AddCopy(0, 0, -1)] = water;
        Require(Find()?.Equals(pos.AddCopy(0, 0, -1)) == true, "Lakebed selection failed to expose visible water in front.");
        solids[pos] = new Block { BlockId = 6, Code = new("game:stonebricks-granite"), BlockMaterial = EnumBlockMaterial.Stone };
        Require(Find() is null, "Underwater building lost its native information.");
        solids[pos] = stone; solids[pos.AddCopy(0, 0, -2)] = stone;
        Require(Find() is null, "Hover looked through a nearer solid obstruction.");
        solids.Remove(pos.AddCopy(0, 0, -2));
        fluids.Clear(); var otherDimension = pos.AddCopy(0, 0, -1); otherDimension.dimension = 1;
        fluids[otherDimension] = water; entity.Pos.Dimension = 1;
        selection.Position = pos.Copy(); selection.Position.dimension = 1;
        Require(Find()?.Equals(otherDimension) == true, "Water hover read another dimension's fluid layer.");
        {
            solids.Clear();
            var containerRay = targetType.GetMethod("TryFindLiquidContainerAlongRay", Hidden)!;
            solids[cornerWater] = bucket;
            object?[] containerArgs = [world, new Vec3d(.95, .8, .5), new Vec3d(1.4, 1.8, .5), null];
            Require((bool)containerRay.Invoke(null, containerArgs)! && cornerWater.Equals(containerArgs[3]),
                "Container lookup skipped the short voxel intersection.");
            solids[new BlockPos(0, 0, 0, 0)] = stone;
            Require(!(bool)containerRay.Invoke(null, containerArgs)!, "Container lookup crossed a solid wall.");
            Console.WriteLine("PASS water/container grid traversal: corners, negative boundaries, endpoints, invalid rays, occlusion and dimensions");
        }

        // Install the actual client Harmony hooks against the native game assemblies.
        var hudType = typeof(SeraphModSystem).Assembly.GetType("Seraph.Client.NativeWaterInfoHud", true)!;
        using var hud = (IDisposable)Activator.CreateInstance(hudType, Hidden, null,
            [api, (System.Func<BlockPos, string>)(_ => throw new Exception("Container queried natural water network.")), null], null)!;
        Require(finalizeLevel is not null, "Modded container hooks were not scheduled after world asset loading.");
        finalizeLevel!();
        finalizeLevel(); // Multiple world loads must not duplicate the postfix.
        var item = new Item { Code = new("game:waterportion"), ItemId = 100,
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        typeof(CollectibleObject).GetField("api", Hidden)!.SetValue(bucket, api);
        var be = new BlockEntityBucket { Block = bucket, Pos = pos, Api = api };
        containers[pos] = be;
        be.Inventory[0].Itemstack = new ItemStack(item, 250);
        var metadata = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.WaterMetadata", true)!;
        metadata.GetMethod("Write")!.Invoke(null, [be.Inventory[0].Itemstack,
            new WaterBatch { VolumeL = 2.5, TemperatureC = 27, Source = "surface", EntericHazardPerL = .02 }]);
        var info = bucket.GetPlacedBlockInfo(world, pos, null!);
        var bucketLine = "Bucket content\n" + Lang.Get("Contents:") + " 2.50 L";
        Require(info.StartsWith(bucketLine) && info.Contains("water drinking advice"),
            "Placed container did not append synchronized water quality.");
        Require(!info.Contains("water volume"), "Container quality duplicated the native contents volume line.");
        be.Inventory[0].Itemstack = null;
        Require(bucket.GetPlacedBlockInfo(world, pos, null!) == bucketLine, "Empty container retained stale water information.");
        typeof(CollectibleObject).GetField("api", Hidden)!.SetValue(barrel, api);
        typeof(CollectibleObject).GetField("api", Hidden)!.SetValue(item, api);
        var barrelEntity = new BlockEntityBarrel { Block = barrel, Pos = pos, Api = api };
        containers[pos] = barrelEntity;
        solids[pos] = barrel;
        barrelEntity.Inventory[1].Itemstack = new ItemStack(item, 500);
        metadata.GetMethod("Write")!.Invoke(null, [barrelEntity.Inventory[1].Itemstack,
            new WaterBatch { VolumeL = 5, TemperatureC = 12, Source = "surface", EntericHazardPerL = .05 }]);
        Require((bool)isWaterContainer.Invoke(null, [world, pos])!, "Open water-filled wooden barrel was not recognized as an interaction source.");
        barrelEntity.Sealed = true;
        Require(!(bool)isWaterContainer.Invoke(null, [world, pos])!, "Sealed barrel was exposed as drinkable/wash water.");
        barrelEntity.Sealed = false;
        var barrelInfo = barrel.GetPlacedBlockInfo(world, pos, null!);
        Require(barrelInfo.Contains("water drinking advice") && !barrelInfo.Contains("water volume"),
            "Native barrel override lost or duplicated water quality from its liquid slot.");
        var bedQuality = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.BedRestProperties", true)!
            .GetMethod("Quality", Hidden)!;
        var previous = 0d;
        foreach (var efficiency in new[] { .58333333, .70833333, .79166666 })
        {
            var bed = new BlockBed { Code = new("game:bed-wood-head-north"),
                Attributes = JsonObject.FromJson("{\"sleepEfficiency\":" + efficiency.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}") };
            var quality = (double)bedQuality.Invoke(null, [bed])!;
            Require(quality > previous && quality <= 1, "Different beds lost their rest quality ordering.");
            previous = quality;
            Require(bed.GetPlacedBlockInfo(world, pos, null!).EndsWith(Lang.Get("vsslssmod:bed-rest-waiting")),
                "Native bed tooltip invented recovery rates before server synchronization.");
            Require(bed.GetPlacedBlockInfo(world, pos, null!).Contains(Lang.Get("Lets you sleep for up to {0} hours", Math.Round((float)efficiency * 24 / 2f, 2))),
                "Bed tooltip lost the native sleep-duration estimate.");
        }
        Console.WriteLine("PASS water hover object priority, occlusion, exact ray, dimensions, container quality/one-line content and native bed rest tooltips");
    }

    private sealed class HoverBucket : BlockBucket
    {
        public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
            => "Bucket content\n" + Lang.Get("Contents:") + "\n   2.50 L";
    }
}
