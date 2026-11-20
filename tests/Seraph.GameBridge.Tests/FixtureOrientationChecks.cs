using System.Reflection;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

internal static class FixtureOrientationChecks
{
    internal static void Run()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> action) => (T)RoutingProxy.Make(typeof(T), action);
        var master = new BlockBathtub { BlockId = 10, Code = new("vsslssmod:bathtub"), CollisionBoxes = [new(0,0,0,1,1,1)] };
        var part = new BlockBathtub { BlockId = 11, Code = new("vsslssmod:bathtub-part"), CollisionBoxes = master.CollisionBoxes };
        var toilet = new BlockLatrine { BlockId = 12, Code = new("vsslssmod:latrine"), CollisionBoxes = master.CollisionBoxes };
        var wellhead = new BlockWellHead { BlockId = 13, Code = new("vsslssmod:wellhead"), CollisionBoxes = master.CollisionBoxes };
        var air = new Block { Code = new("game:air"), Replaceable = 10000 };
        var grass = new Block { Code = new("game:tallgrass"), BlockId = 2, Replaceable = 10000 };
        var blocks = new Dictionary<BlockPos, Block>();
        var entities = new Dictionary<BlockPos, BlockEntity>();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? new DummySlot() : throw new Exception(m.Name));
        var player = FixturePlayer.Make(new EntityPlayer(), inventory);
        IWorldAccessor world = null!;
        var failMaster = false;
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreAPI>((m, _) => m.Name == "get_Logger" ? logger : throw new Exception(m.Name));
        var accessor = Proxy<IBlockAccessor>((m, a) =>
        {
            if (m.Name == "GetBlock" && a![0] is AssetLocation) return part;
            var p = a![0] as BlockPos;
            if (m.Name == "GetBlock") return blocks.GetValueOrDefault(p!, air);
            if (m.Name == "GetBlockEntity") return entities.GetValueOrDefault(p!);
            if (m.Name is "MarkBlockEntityDirty" or "MarkBlockDirty") return null;
            if (m.Name == "SetBlock")
            {
                var id = (int)a[0]!; p = (BlockPos)a[1]!;
                var old = blocks.GetValueOrDefault(p, air);
                blocks[p.Copy()] = air; // Native removal observes the replaced cell as air, BE still present.
                if (old is BlockBathtub) old.OnBlockRemoved(world, p);
                entities.Remove(p);
                if (failMaster && id == 10) throw new InvalidOperationException("Injected engine placement failure");
                var block = id switch { 10 => master, 11 => part, 12 => (Block)toilet, 13 => wellhead, 2 => grass, _ => air };
                blocks[p.Copy()] = block;
                BlockEntity? entity = id switch { 10 or 11 => new BlockEntityBathtub(), 12 => new BlockEntityLatrine(), 13 => new BlockEntityWellHead(), _ => null };
                if (entity is not null) { entity.Pos = p.Copy(); entity.Block = block; entities[p.Copy()] = entity; }
                return null;
            }
            throw new Exception(m.Name);
        });
        var claims = Proxy<ILandClaimAPI>((_, _) => true);
        world = Proxy<IWorldAccessor>((m, _) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Claims" => claims, "get_Api" => api,
            "get_Side" => EnumAppSide.Server,
            "GetIntersectingEntities" => Array.Empty<Entity>(), _ => throw new Exception(m.Name)
        });
        var anchor = new BlockPos(20, 10, 20, 0);
        var expected = new[] { (0, -1), (1, 0), (0, 1), (-1, 0) };
        var orient = typeof(BlockBathtub).Assembly.GetType("Seraph.World.FixtureOrientation", true)!;
        for (var d = 0; d < 4; d++)
        {
            blocks.Clear(); entities.Clear();
            player.Entity.Pos.Yaw = -d * GameMath.PIHALF;
            var yaw = player.Entity.Pos.Yaw;
            var failure = "";
            Require(master.TryPlaceBlock(world, player, new(master), new() { Position = anchor }, ref failure), "Bath placement failed: " + failure);
            var bath = entities.Values.OfType<BlockEntityBathtub>().Single(e => e.Block == master);
            var companion = entities.Values.OfType<BlockEntityBathtub>().Single(e => e.Block == part);
            Require(companion.Pos.X - bath.Pos.X == expected[d].Item1 && companion.Pos.Z - bath.Pos.Z == expected[d].Item2,
                "Bath long axis does not follow player heading.");
            Require(ReferenceEquals(typeof(BlockBathtub).GetMethod("GetMaster", all,null,[typeof(IWorldAccessor),typeof(BlockPos)],null)!.Invoke(null, [world, companion.Pos]), bath), "Rotated companion did not resolve its reservoir.");
            var box = master.GetSelectionBoxes(accessor, bath.Pos).Single();
            var pbox = part.GetSelectionBoxes(accessor, companion.Pos).Single();
            Require(Math.Abs(bath.Pos.X + box.X1 - companion.Pos.X - pbox.X1) < 1e-6
                && Math.Abs(bath.Pos.Z + box.Z2 - companion.Pos.Z - pbox.Z2) < 1e-6, "Rotated selection bounds diverge.");
            var center = new Vec3d((bath.Pos.X + companion.Pos.X + 1) / 2d, 10.2, (bath.Pos.Z + companion.Pos.Z + 1) / 2d);
            Require((bool)typeof(BlockEntityBathtub).GetMethod("Contains", all)!.Invoke(bath, [center, 0])!, "Rotated bath rejects its interior.");
            var saved = new TreeAttribute(); bath.ToTreeAttributes(saved);
            var restored = new BlockEntityBathtub { Block = master, Pos = bath.Pos.Copy() }; restored.FromTreeAttributes(saved, world);
            Require((int)typeof(BlockEntityBathtub).GetProperty("Direction", all)!.GetValue(restored)! == ((d + 3) & 3), "Bath lost direction on load.");
            foreach (var breakPart in new[] { false, true })
            {
                if (breakPart) { blocks[bath.Pos] = master; blocks[companion.Pos] = part; entities[bath.Pos] = bath; entities[companion.Pos] = companion; }
                accessor.SetBlock(0, breakPart ? companion.Pos : bath.Pos);
                Require(!blocks.Values.OfType<BlockBathtub>().Any(), "Breaking rotated bath left half behind.");
            }
            Require(toilet.TryPlaceBlock(world, player, new(toilet), new() { Position = anchor }, ref failure), "Latrine placement failed.");
            var latrine = (BlockEntityLatrine)entities[anchor]; var tree = new TreeAttribute(); latrine.ToTreeAttributes(tree);
            Require(tree.GetInt("direction") == d && player.Entity.Pos.Yaw == yaw, "Latrine direction or camera changed.");
            Require(orient.GetField("PlacementDirection", all)!.GetValue(null) is null, "Placement scope leaked.");
            accessor.SetBlock(0, anchor);
            Require(wellhead.TryPlaceBlock(world, player, new(wellhead), new() { Position = anchor }, ref failure), "Wellhead placement failed.");
            var well = (BlockEntityWellHead)entities[anchor]; var wellTree = new TreeAttribute(); well.ToTreeAttributes(wellTree);
            Require(wellTree.GetInt("direction") == d && player.Entity.Pos.Yaw == yaw, "Wellhead does not follow view heading or moved camera.");
            var loadedWell = new BlockEntityWellHead { Block = wellhead, Pos = anchor.Copy() };
            loadedWell.FromTreeAttributes(wellTree, world);
            var roundtrip = new TreeAttribute(); loadedWell.ToTreeAttributes(roundtrip);
            Require(roundtrip.GetInt("direction") == d, "Wellhead lost orientation on save/load.");
            loadedWell.FromTreeAttributes(new TreeAttribute(), world); roundtrip = new TreeAttribute(); loadedWell.ToTreeAttributes(roundtrip);
            Require(roundtrip.GetInt("direction") == 0, "Old wells changed orientation.");
        }
        blocks.Clear(); entities.Clear(); player.Entity.Pos.Yaw = 0;
        blocks[anchor.Copy()] = grass; var behind = anchor.AddCopy(0, 0, 1); blocks[behind] = grass;
        failMaster = true; var code = "";
        Require(!master.TryPlaceBlock(world, player, new(master), new() { Position = anchor }, ref code), "Injected failure succeeded.");
        Require(blocks[anchor] == grass && blocks[behind] == grass && !entities.Any(), "Placement rollback lost replaceable blocks or left a partial bath.");
        Require(orient.GetField("PlacementDirection", all)!.GetValue(null) is null, "Exceptional placement leaked direction.");
        var legacy = new BlockEntityBathtub { Block = master, Pos = anchor.Copy() }; legacy.FromTreeAttributes(new TreeAttribute(), world);
        Require((int)typeof(BlockEntityBathtub).GetProperty("Direction", all)!.GetValue(legacy)! == 0, "Legacy bath orientation changed.");
        Console.WriteLine("PASS actual bath/latrine/wellhead placement follows four view headings on all clicked faces, shared selection/interior, both-cell teardown, persistence, legacy direction, camera retention and replaceable-block rollback");
    }
}
