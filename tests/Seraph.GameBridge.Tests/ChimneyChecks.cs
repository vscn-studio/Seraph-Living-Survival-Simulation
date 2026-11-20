using System.Reflection;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

internal static class ChimneyChecks
{
    private sealed class NamedHostBlock : Block
    {
        public override string GetHeldItemName(ItemStack stack) => "Granite rock";
        public override int GetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type) => 7;
    }

    internal static void Run()
    {
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var pos = new BlockPos(10, 20, 30, 0);
        var host = new NamedHostBlock { Code = new("game:rock-granite"), BlockId = 10,
            SideSolid = new SmallBoolArray(63), MatterState = EnumMatterState.Solid, CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)] };
        var air = new Block { Code = new("game:air"), BlockId = 0, Replaceable = 10000 };
        var chimney = new BlockChimney { Code = new("vsslssmod:chimney"), BlockId = 11 };
        Block middle = host, above = air, top = air, below = air;
        BlockEntity? entity = null;
        var writes = new List<BlockPos>();
        var allowed = true;
        var createEntity = true;
        var throwOnInstall = false;
        var throwOnNotify = false;
        var throwOnDirty = false;
        var initialized = true;
        var notifications = 0;
        var slot = new DummySlot { Itemstack = new ItemStack(chimney, 2) };
        var actor = new EntityPlayer();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : throw new Exception(m.Name));
        var player = FixturePlayer.Make(actor, inventory);
        ICoreServerAPI api = null!;
        var accessor = Proxy<IBlockAccessor>((m, a) =>
        {
            switch (m.Name)
            {
                case "GetBlock":
                    var p = (BlockPos)a![0]!;
                    return p.Y == pos.Y ? middle : p.Y == pos.Y + 1 ? above : p.Y > pos.Y + 1 ? top : below;
                case "GetBlockEntity": return ((BlockPos)a![0]!).Equals(pos) ? entity : null;
                case "SetBlock":
                    var target = (BlockPos)a![1]!; writes.Add(target.Copy());
                    Require(target.Equals(pos), "Embedded chimney wrote a companion cell.");
                    middle = (int)a[0]! == chimney.Id ? chimney : host;
                    entity = middle == chimney && createEntity ? new BlockEntityChimney { Api = initialized ? api : null!, Block = chimney, Pos = pos } : null;
                    if (middle == chimney && throwOnInstall) throw new InvalidOperationException("Simulated installation callback failure");
                    return null;
                case "MarkBlockEntityDirty": case "MarkBlockDirty":
                    if (throwOnDirty) throw new InvalidOperationException("Host persistence callback failed");
                    return null;
                case "TriggerNeighbourBlockUpdate":
                    notifications++;
                    if (throwOnNotify) throw new InvalidOperationException("Neighbor callback failed");
                    return null;
                case "GetDecors": return null;
                default: throw new Exception("Unexpected chimney accessor " + m.Name);
            }
        });
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? allowed : throw new Exception(m.Name));
        var world = Proxy<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Claims" => claims, "get_Side" => EnumAppSide.Server,
            "GetBlock" => (int)a![0]! == host.Id ? host : chimney,
            "PlayerByUid" => player, "PlaySoundAt" => null,
            _ => throw new Exception("Unexpected chimney world " + m.Name)
        });
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_Side" => EnumAppSide.Server, _ => throw new Exception(m.Name) });
        actor.World = world;
        typeof(CollectibleObject).GetField("api", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(chimney, api);
        void Interact(BlockFacing face)
        {
            var handling = EnumHandHandling.NotHandled;
            chimney.OnHeldInteractStart(slot, actor, new BlockSelection { Position = pos, Face = face }, null, true, ref handling);
            Require(handling == EnumHandHandling.PreventDefault, "Chimney allowed ordinary placement.");
        }
        var failure = "";
        Require(!chimney.TryPlaceBlock(world, player, slot.Itemstack!, new BlockSelection { Position = pos }, ref failure),
            "Chimney still places as a three-cell freestanding structure.");
        Interact(BlockFacing.UP);
        Require(writes.Count == 0 && slot.StackSize == 2, "Top-face interaction embedded a chimney.");
        allowed = false; Interact(BlockFacing.DOWN); allowed = true;
        Require(writes.Count == 0 && slot.StackSize == 2, "Claim denial consumed or installed a chimney.");
        above = host;
        Require(!BlockChimney.CanEmbedAt(world, pos, out _, out failure) && failure.EndsWith("need-air"), "Blocked outlet accepted.");
        above = air; below = host;
        Require(!BlockChimney.CanEmbedAt(world, pos, out _, out _), "Lower grille intersects a solid block.");
        below = air;
        top = host;
        Require(!BlockChimney.CanEmbedAt(world, pos, out _, out failure) && failure.EndsWith("need-air"),
            "Blocked upper flue mouth accepted.");
        top = air;
        entity = new BlockEntityChimney();
        Require(!BlockChimney.CanEmbedAt(world, pos, out _, out _), "Container/block-entity host accepted.");
        entity = null;
        Require(!BlockChimney.IsFullSolidCube(new Block { BlockId = 12, SideSolid = new SmallBoolArray(63),
            CollisionBoxes = [new Cuboidf(0, 0, 0, 1, .5f, 1)] }), "Half-block host accepted.");
        createEntity = false;
        Interact(BlockFacing.DOWN);
        Require(middle == host && slot.StackSize == 2 && entity is null, "Missing entity consumed an item or lost the host.");
        createEntity = true;
        throwOnInstall = true;
        Interact(BlockFacing.DOWN);
        Require(middle == host && slot.StackSize == 2 && entity is null,
            "Installation exception did not restore the host and conserve the item.");
        throwOnInstall = false;
        foreach (var failureStage in new[] { "initialize", "persist", "notify" })
        {
            initialized = failureStage != "initialize";
            throwOnDirty = failureStage == "persist"; throwOnNotify = failureStage == "notify";
            Interact(BlockFacing.DOWN);
            Require(middle == host && slot.StackSize == 2 && entity is null, "Failed stage lost host or item: " + failureStage);
        }
        initialized = true; throwOnDirty = throwOnNotify = false;
        var stale = new ChimneyInstallationPlan(pos, host);
        middle = air;
        Require(!stale.Execute(accessor, chimney, slot, () => throw new Exception("Stale plan committed")) && slot.StackSize == 2,
            "A stale plan overwrote a changed host.");
        middle = host;
        Require(BlockChimney.IsFullSolidCube(new Block { BlockId = 15, SideSolid = new SmallBoolArray(63),
            CollisionBoxes = [new Cuboidf(0, 0, 0, .5f, 1, 1), new Cuboidf(.5f, 0, 0, 1, 1, 1)] }), "Multi-box full host rejected.");
        Require(!BlockChimney.IsFullSolidCube(new Block { BlockId = 15, SideSolid = new SmallBoolArray(63),
            CollisionBoxes = [new Cuboidf(0, 0, 0, .4f, 1, 1), new Cuboidf(.6f, 0, 0, 1, 1, 1)] }), "Cavity host accepted.");
        Require(!BlockChimney.IsFullSolidCube(new Block { BlockId = 15, SideSolid = new SmallBoolArray(63), CollisionBoxes = [] }), "Collision-free host accepted.");
        Require(!BlockChimney.IsFullSolidCube(new Block { BlockId = 15, MatterState = EnumMatterState.Liquid,
            SideSolid = new SmallBoolArray(63), CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)] }), "Liquid host accepted.");
        notifications = 0;
        writes.Clear();
        Interact(BlockFacing.DOWN);
        Require(notifications == 1 && writes.Count == 1 && slot.StackSize == 1 && entity is BlockEntityChimney { HostStack.Block: var saved } && saved == host,
            "Bottom installation did not preserve the host or consume exactly one chimney.");
        Require(!stale.Execute(accessor, chimney, slot, () => { }) && slot.StackSize == 1, "Installed plan replay consumed another item.");
        var isolated = ((BlockEntityChimney)entity!).HostStack!; isolated.StackSize = 30;
        Require(((BlockEntityChimney)entity).HostStack!.StackSize == 1, "External code mutated the host snapshot.");
        var tree = new TreeAttribute(); entity!.ToTreeAttributes(tree);
        var restored = new BlockEntityChimney { Block = chimney, Pos = pos };
        restored.FromTreeAttributes(tree, world);
        entity = restored;
        Require(chimney.GetRetention(pos, BlockFacing.UP, default) == 7, "Host thermal retention was not forwarded.");
        var drops = chimney.GetDrops(world, pos, player);
        Require(drops.Length == 2 && drops[0].Block == host && drops[1].Block == chimney && drops.All(s => s.StackSize == 1),
            "Saved embedded chimney does not return its original host and one chimney.");
        Require(!BlockChimney.CanEmbedAt(world, pos, out _, out _), "Chimney accepted embedding into itself.");
        var info = chimney.GetPlacedBlockInfo(world, pos, player);
        Require(info.Contains(Lang.Get("vsslssmod:chimney-host", host.GetHeldItemName(new ItemStack(host))))
            && info.Contains(Lang.Get("vsslssmod:chimney-vent"))
            && info.Contains(Lang.Get("vsslssmod:chimney-openings", Lang.Get("vsslssmod:chimney-clear"), Lang.Get("vsslssmod:chimney-clear"))),
            "Chimney hover omitted host, vent capability, or clear openings.");
        above = host;
        Require(chimney.GetPlacedBlockInfo(world, pos, player).Contains(Lang.Get("vsslssmod:chimney-openings",
            Lang.Get("vsslssmod:chimney-clear"), Lang.Get("vsslssmod:chimney-clear"))), "Blocked inner cell changed the visible top outlet state.");
        above = air; top = host;
        Require(chimney.GetPlacedBlockInfo(world, pos, player).Contains(Lang.Get("vsslssmod:chimney-openings",
            Lang.Get("vsslssmod:chimney-clear"), Lang.Get("vsslssmod:chimney-blocked"))), "Blocked top mouth still displays as clear.");
        top = air;
        below = host; above = air;
        Require(chimney.GetPlacedBlockInfo(world, pos, player).Contains(Lang.Get("vsslssmod:chimney-openings",
            Lang.Get("vsslssmod:chimney-blocked"), Lang.Get("vsslssmod:chimney-clear"))), "Blocked inlet still displays as clear.");
        below = air;
        var rendered = new List<MeshData>();
        var tessellated = new List<Block>();
        var mesher = Proxy<ITerrainMeshPool>((m, a) =>
        { Require(m.Name == "AddMeshData", "Unexpected terrain pool call."); rendered.Add((MeshData)a![0]!); return null; });
        var tesselator = Proxy<ITesselatorAPI>((m, a) =>
        {
            Require(m.Name == "TesselateBlock", "Unexpected chimney tesselator call.");
            tessellated.Add((Block)a![0]!);
            a[1] = CubeMeshUtil.GetCubeOnlyScaleXyz(1, 1, new Vec3f());
            return null;
        });
        Require(restored.OnTesselation(mesher, tesselator) && rendered.Count == 2
            && tessellated.SequenceEqual(new Block[] { host, chimney }), "Embedded host or chimney mesh is missing.");
        restored.OnTesselation(mesher, tesselator);
        Require(tessellated.Count == 2 && rendered.Count == 4 && !ReferenceEquals(rendered[0], rendered[2]), "Mesh cache did not isolate submitted meshes.");
        restored.FromTreeAttributes(tree, world);
        restored.OnTesselation(mesher, tesselator);
        Require(tessellated.Count == 4, "Reload did not invalidate host mesh cache.");
        Console.WriteLine("PASS chimney bottom-only embedded placement, claims, clearance, host persistence and conserved drops");
    }
}
