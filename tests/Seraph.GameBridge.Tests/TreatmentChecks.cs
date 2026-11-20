using System.Reflection;
using System.Text.Json;
using Seraph;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class TreatmentChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    internal static void Run(string root, string gamePath)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var gameHour = 0d;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name switch { "get_TotalHours" => (object)gameHour, "get_HoursPerDay" => (object)24f, _ => throw new Exception(m.Name) });
        var cells = new Dictionary<BlockPos, BlockEntityWaterTreatment>();
        var air = new Block { Code = new("game:air"), BlockId = 0, Replaceable = 10000 };
        var cell = new BlockWaterTreatment { Code = new("vsslssmod:watertreatment"), BlockId = 50 };
        var filter = new BlockWaterTreatment { Code = new("vsslssmod:filterpot"), BlockId = 51 };
        var drain = new BlockWaterTreatment { Code = new("vsslssmod:drainpot"), BlockId = 52 };
        var baths = new Dictionary<BlockPos, BlockEntityBathtub>();
        var native = new Dictionary<BlockPos, BlockEntityContainer>();
        var bucket = new BlockBucket { Code = new("game:bucket-wood"), BlockId = 60, Attributes = JsonObject.FromJson("{\"isTopOpened\":true}") };
        var waterItem = new Item { Code = new("game:waterportion"), ItemId = 100, Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        var saltItem = new Item { Code = new("game:saltwaterportion"), ItemId = 101, Attributes = waterItem.Attributes };
        var slot = new DummySlot();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name switch { "get_ActiveHotbarSlot" => slot, "get_InventoriesOrdered" => Array.Empty<InventoryBase>(), _ => throw new Exception(m.Name) });
        var player = FixturePlayer.Make(new EntityPlayer(), inventory);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        var allowed = true;
        var loaded = true;
        BlockPos? deniedPos = null, unloadedPos = null;
        var particles = 0;
        var drops = new List<ItemStack>();
        var claims = Proxy<ILandClaimAPI>((m, a) => m.Name == "TryAccess"
            ? allowed && !(a![1] is BlockPos p && p.Equals(deniedPos)) : throw new Exception(m.Name));
        ICoreServerAPI api = null!;
        IServerWorldAccessor world = null!;
        var chunkType = typeof(IBlockAccessor).GetMethod("GetChunkAtBlockPos", [typeof(BlockPos)])!.ReturnType;
        var chunk = RoutingProxy.Make(chunkType, (m, _) => throw new Exception(m.Name));
        var accessor = Proxy<IBlockAccessor>((m, a) =>
        {
            var p = a is { Length: > 0 } && a[0] is BlockPos pos ? pos : null;
            switch (m.Name)
            {
                case "GetChunkAtBlockPos": return loaded && !p!.Equals(unloadedPos) ? chunk : null;
                case "GetBlockEntity": return (BlockEntity?)cells.GetValueOrDefault(p!) ?? (BlockEntity?)native.GetValueOrDefault(p!) ?? baths.GetValueOrDefault(p!);
                case "GetBlock": return cells.ContainsKey(p!) ? cell : native.ContainsKey(p!) ? native[p!].Block : baths.ContainsKey(p!) ? baths[p!].Block : air;
                case "SetBlock":
                    var id = (int)a![0]!; p = (BlockPos)a[1]!;
                    if (id == 0) { if (cells.TryGetValue(p, out var old)) cell.OnBlockRemoved(world!, p); cells.Remove(p); }
                    else cells[p.Copy()] = new BlockEntityWaterTreatment { Api = api, Block = cell, Pos = p.Copy() };
                    return null;
                case "MarkBlockDirty": case "MarkBlockEntityDirty": case "GetClimateAt": return null;
                default: throw new Exception("Unexpected treatment accessor " + m.Name);
            }
        });
        world = Proxy<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_Calendar" => calendar, "get_BlockAccessor" => accessor, "get_Claims" => claims, "get_Side" => EnumAppSide.Server,
            "GetBlock" => ((AssetLocation)a![0]!).Path switch { "filterpot" => filter, "drainpot" => drain, _ => cell },
            "get_Blocks" => new Block[] { bucket },
            "GetItem" => a![0] is AssetLocation c ? (c.Path == "saltwaterportion" ? saltItem : waterItem) : (int)a[0]! == 101 ? saltItem : waterItem,
            "GetIntersectingEntities" => Array.Empty<Entity>(), "PlayerByUid" => player,
            "SpawnItemEntity" => Drop((ItemStack)a![0]!), "SpawnParticles" => Particle(a!),
            _ => throw new Exception("Unexpected treatment world " + m.Name)
        });
        object? Drop(ItemStack stack) { drops.Add(stack); return null; }
        object? Particle(object?[] args)
        {
            Require(args[0] is SimpleParticleProperties { WithTerrainCollision: false, ShouldDieInLiquid: true,
                MinVelocity.Y: < 0, LifeLength: > 0, GravityEffect: 0 }, "Flow particles can stick to a reserved fixture collision box.");
            particles++; return null;
        }
        // The native network roundtrip silently restores terrain collision.
        // Exercise our BE packet on a client instead of inspecting a server particle.
        using (var bytes = new MemoryStream())
        {
            new SimpleParticleProperties { MinPos = new Vec3d(), WithTerrainCollision = false }.ToBytes(new BinaryWriter(bytes));
            bytes.Position = 0;
            var decoded = new SimpleParticleProperties(); decoded.FromBytes(new BinaryReader(bytes), world);
            Require(decoded.WithTerrainCollision, "Native particle wire format changed; reassess client flow workaround.");
        }
        var clientWorld = Proxy<IClientWorldAccessor>((m, a) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Side" => EnumAppSide.Client,
            "SpawnParticles" => Particle(a!), _ => throw new Exception("Unexpected client flow world " + m.Name)
        });
        var clientApi = Proxy<ICoreClientAPI>((m, _) => m.Name switch
        { "get_World" => clientWorld, "get_Side" => EnumAppSide.Client, _ => throw new Exception(m.Name) });
        var network = Proxy<IServerNetworkAPI>((m, a) =>
        {
            Require(m.Name == "BroadcastBlockEntityPacket", "Unexpected treatment network call.");
            var p = (BlockPos)a![0]!;
            var visual = new BlockEntityWaterTreatment { Api = clientApi, Block = cell, Pos = p };
            var tree = new TreeAttribute(); cells[p].ToTreeAttributes(tree); visual.FromTreeAttributes(tree, clientWorld);
            visual.OnReceivedServerPacket((int)a[1]!, (byte[])a[2]!);
            return null;
        });
        var mod = new SeraphModSystem();
        var recipes = new RecipeRegistrySystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? m.GetGenericArguments()[0] == typeof(RecipeRegistrySystem) ? (object)recipes : mod : throw new Exception(m.Name));
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server, "get_Network" => network, _ => throw new Exception(m.Name) });
        typeof(SeraphModSystem).GetField("server", Hidden)!.SetValue(mod,
            Activator.CreateInstance(typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!, [api]));
        typeof(CollectibleObject).GetField("api", Hidden)!.SetValue(bucket, api);
        typeof(BlockLiquidContainerTopOpened).GetField("Props", Hidden)!.SetValue(bucket, new LiquidTopOpenContainerProps { CapacityLitres = 10 });
        foreach (var item in new Item[] { waterItem, saltItem }) typeof(CollectibleObject).GetField("api", Hidden)!.SetValue(item, api);
        player.Entity.World = world;
        var adapterType = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.NativeWaterTransfer", true)!;
        using var waterTransfer = (IDisposable)Activator.CreateInstance(adapterType, [api])!;
        adapterType.GetMethod("RegisterBlocks")!.Invoke(waterTransfer, null);
        var pos = new BlockPos(10, 20, 30, 0);
        var kindType = typeof(BlockWaterTreatment).Assembly.GetType("Seraph.World.TreatmentKind", true)!;
        bool Place(string kind, BlockPos target, int h, int d)
            => (bool)typeof(BlockWaterTreatment).GetMethod("Place", Hidden)!.Invoke(null,
                [world, player, Enum.Parse(kindType, kind), target, h, d, ""])!;
        Array Slots(BlockEntityWaterTreatment e) => (Array)typeof(BlockEntityWaterTreatment).GetProperty("Slots", Hidden)!.GetValue(e)!;
        object State(BlockEntityWaterTreatment e, int h) => Slots(e).GetValue(h)!;
        object? Get(object s, string key) => s.GetType().GetProperty(key, Hidden)!.GetValue(s);
        void Set(object s, string key, object? value) => s.GetType().GetProperty(key, Hidden)!.SetValue(s, value);
        double Volume(object s) => ((WaterBatch?)Get(s, "Water"))?.VolumeL ?? 0;
        object? Call(BlockEntityWaterTreatment e, string method, params object?[] a)
        {
            if (method == "Tick")
            {
                var clock = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.WorldCalendarTime")!.GetMethod("Seconds", Hidden)!;
                var stamp = typeof(BlockEntityWaterTreatment).GetField("settledGameSeconds", Hidden)!;
                stamp.SetValue(e, (double)clock.Invoke(null, [world])!);
                gameHour += 30d / 3600;
            }
            return typeof(BlockEntityWaterTreatment).GetMethod(method, Hidden)!.Invoke(e, a);
        }
        for (var d = 0; d < 4; d++)
        {
            cells.Clear(); drops.Clear();
            player.Entity.Pos.Yaw = -d * GameMath.PIHALF;
            var headingFailure = "";
            Require(drain.TryPlaceBlock(world, player, new ItemStack(drain), new BlockSelection { Position = pos, Face = BlockFacing.UP }, ref headingFailure), "Drain could not place from player view.");
            var delta = d switch { 0 => (0, -1), 1 => (1, 0), 2 => (0, 1), _ => (-1, 0) };
            var pipe = pos.AddCopy(delta.Item1, 0, delta.Item2);
            Require(cells.Count == 2 && Get(State(cells[pipe], 0), "Kind")!.ToString() == "Pipe", "Rotated pipe reservation incorrect.");
            Require(Place("Filter", pos, 1, d), "Filter did not share the upper half with drain.");
            Require(!Place("Filter", pos, 1, d), "Occupied half accepted another fixture.");
            var boxes = cell.GetSelectionBoxes(accessor, pos);
            Require(boxes.Length == 2 && boxes[0].Y2 == .5f && boxes[1].Y1 == .5f && boxes[1].Y2 == 1,
                "Selection did not separate half-height fixtures.");
            var outlet = (Vec3d)Call(cells[pos], "Outlet", 0)!;
            Require((d == 0 ? outlet.Z < pos.Z : d == 1 ? outlet.X > pos.X + 1 : d == 2 ? outlet.Z > pos.Z + 1 : outlet.X < pos.X)
                && Math.Abs(outlet.Y - (pos.Y + 1d/18)) < 1e-6, "Particle outlet did not follow the pipe tip.");
            // Removing the pipe removes only its linked basin, preserving the upper filter.
            Call(cells[pipe], "Remove", 0, true);
            Require(cells.Count == 1 && Get(State(cells[pos], 0), "Kind")!.ToString() == "Empty"
                && Get(State(cells[pos], 1), "Kind")!.ToString() == "Filter"
                && drops.Count == 1 && drops[0].Block == drain, "Breaking pipe lost/duplicated another half or its fixture drop.");
        }
        // Removing a whole pipe cell must not delete the basin's unrelated upper half.
        cells.Clear(); drops.Clear();
        Require(Place("Drain", pos, 0, 0) && Place("Filter", pos, 1, 0), "Whole-cell teardown setup failed.");
        accessor.SetBlock(0, pos.AddCopy(0, 0, -1));
        Require(cells.Count == 1 && Get(State(cells[pos], 0), "Kind")!.ToString() == "Empty"
            && Get(State(cells[pos], 1), "Kind")!.ToString() == "Filter" && drops.Count == 1,
            "Whole-cell teardown left orphan pipes or deleted another half.");
        cells.Clear();
        allowed = false; Require(!Place("Drain", pos, 0, 0) && cells.Count == 0, "Claim rejection left a partial fixture."); allowed = true;
        loaded = false; Require(!Place("Drain", pos, 0, 0) && cells.Count == 0, "Unloaded cells accepted placement."); loaded = true;
        Require(Place("Filter", pos, 0, 0) && Place("Filter", pos, 1, 0)
            && cell.GetSelectionBoxes(accessor, pos).Length == 2, "Two filters cannot fit inside one block.");
        cells.Clear();
        Require(Place("Drain", pos, 0, 0) && Place("Filter", pos, 1, 0), "Failed to construct filter assembly.");
        var assembly = cells[pos];
        // Isolate filter-to-basin checks from the open drain's intentional waste.
        unloadedPos = pos.AddCopy(0, -1, -1);
        var fs = State(assembly, 1); var ds = State(assembly, 0);
        var insert = fs.GetType().GetMethod("TryInsert", Hidden)!;
        bool Insert(object s, CollectibleObject item, int count, int expected = 0)
        {
            object?[] args = [new ItemStack(item, count), 0];
            var result = (bool)insert.Invoke(s, args)!;
            if (result) Require((int)args[1]! == expected, "Wrong media consumption.");
            return result;
        }
        var gravel = new Block { Code = new("game:gravel-granite"), BlockId = 100 };
        var coal = new Item { Code = new("game:charcoal"), ItemId = 101 };
        var linen = new Block { Code = new("game:linen-normal-down"), BlockId = 102 };
        Require(!Insert(fs, coal, 2) && !Insert(fs, linen, 1), "Filter accepted layers out of order.");
        Require(!Insert(ds, gravel, 1) && !Insert(ds, coal, 2) && Insert(ds, linen, 1, 1), "Drain did not enforce cloth-only media.");
        Require(Insert(fs, gravel, 1, 1) && !Insert(fs, coal, 1) && Insert(fs, coal, 2, 2) && Insert(fs, linen, 1, 1),
            "Three-layer filter could not assemble.");
        Require(!Insert(fs, linen, 1), "Duplicate layer accepted.");
        var raw = new WaterBatch { VolumeL = 1, TemperatureC = 25, EntericHazardPerL = 2, Turbidity = .8, SaltGramsPerL = 35, ChemicalHazardPerL = .4 };
        Set(fs, "Water", raw);
        Call(assembly, "Tick", 1f);
        Require(Math.Abs(Volume(fs) + Volume(ds) - 1) < 1e-9 && Volume(fs) == .975 && Volume(ds) == .025
            && (double)Get(fs, "UsedL")! == .025, "Gravity transfer changed volume or media consumption.");
        var product = (WaterBatch)Get(ds, "Water")!;
        Require(product.SaltGramsPerL == 35 && product.TemperatureC == 25 && product.EntericHazardPerL is > 0 and < 2
            && product.ChemicalHazardPerL < raw.ChemicalHazardPerL, "Transfer lost water quality treatment/metadata.");
        Require(particles == 0, "Spout particles appeared without receiving container.");
        Set(ds, "Water", raw with { VolumeL = 10 });
        Call(assembly, "Tick", 1f);
        Require(Volume(fs) == .975 && (double)Get(fs, "UsedL")! == .025, "Blocked receiver consumed source/media.");
        Set(ds, "Water", null); Set(fs, "UsedL", 80d);
        Call(assembly, "Tick", 1f); Require(Volume(fs) == .975 && Volume(ds) == 0, "Spent media still filtered water.");
        Set(fs, "UsedL", 0d); allowed = false;
        Call(assembly, "Tick", 1f); Require(Volume(fs) == .975 && Volume(ds) == 0, "Changed claim did not stop automation."); allowed = true;
        loaded = false; Call(assembly, "Tick", 1f); Require(Volume(fs) == .975, "Unloaded receiver consumed water."); loaded = true;
        var saved = new TreeAttribute(); assembly.ToTreeAttributes(saved);
        var restored = new BlockEntityWaterTreatment { Block = cell, Pos = pos, Api = api };
        restored.FromTreeAttributes(saved, world);
        var restoredFilter = State(restored, 1);
        Require(Volume(restoredFilter) == .975 && Get(restoredFilter, "Cloth") is not null && Get(restoredFilter, "OwnerUid")!.ToString() == player.PlayerUID,
            "Half state/media/quality/ownership not persisted.");
        var invalid = new TreeAttribute(); invalid.SetString("treatmentSlots", "[{\"Kind\":1,\"Direction\":99},{}]");
        restored.FromTreeAttributes(invalid, world);
        Require(Enumerable.Range(0, 2).All(h => Get(State(restored, h), "Kind")!.ToString() == "Empty"), "Invalid save accepted.");
        // Exercise actual native liquid-container APIs and the installed metadata patch.
        unloadedPos = null;
        cells.Clear();
        Require(Place("Drain", pos, 0, 0), "Native receiver setup failed.");
        var source = cells[pos]; var sourceState = State(source, 0);
        var receiverPos = pos.AddCopy(0, -1, -1);
        var receiver = new BlockEntityBucket { Block = bucket, Pos = receiverPos, Api = api };
        receiver.Inventory.Api = api; native[receiverPos] = receiver;
        Set(sourceState, "Water", raw);
        var waterText = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.WaterInfoText", true)!.GetMethod("Lines", Hidden)!;
        System.Func<string, object[], string> localize = (key, values) => Vintagestory.API.Config.Lang.Get("vsslssmod:" + key, values);
        var shared = (string[])waterText.Invoke(null, [raw, localize, .25d, false, false])!;
        var potInfo = (string)Call(source, "Info", 0)!;
        Require(shared.All(line => potInfo.Contains(line)),
            "Treatment pot disagrees with the common source/container quality fields.");
        Call(source, "Tick", 1f);
        var actual = bucket.GetContent(receiverPos)!;
        var metadata = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.WaterMetadata", true)!;
        WaterBatch Quality(ItemStack content) => (WaterBatch)metadata.GetMethod("Read")!.Invoke(null, [content, content.StackSize / 100d])!;
        Require(actual.StackSize == 10 && Math.Abs(Volume(sourceState) - .9) < 1e-9 && particles == 1,
            "Native bucket gravity transfer lost/duplicated water or missing outlet particles.");
        var quality = Quality(actual);
        Require(quality.SaltGramsPerL == 35 && quality.EntericHazardPerL == 2 && Math.Abs(quality.Turbidity - .8) < 1e-9,
            "Native receiving bucket lost water quality metadata.");
        receiver.Inventory[0].Itemstack = new ItemStack(saltItem, 990);
        metadata.GetMethod("Write")!.Invoke(null, [receiver.Inventory[0].Itemstack, raw with { VolumeL = 9.9 }]);
        Call(source, "Tick", 1f);
        Require(bucket.GetContent(receiverPos)!.StackSize == 1000 && Math.Abs(Volume(sourceState) - .8) < 1e-9,
            "Nearly full native bucket did not accept exactly its free capacity.");
        var flowCount = particles;
        Call(source, "Tick", 1f);
        Require(Math.Abs(Volume(sourceState) - .7) < 1e-9 && particles == flowCount + 1, "Full bucket stopped the open spout instead of wasting overflow.");
        receiver.Inventory[0].Itemstack = new ItemStack(new Item { Code = new("game:honeyportion"), ItemId = 200 }, 10);
        Call(source, "Tick", 1f);
        Require(Math.Abs(Volume(sourceState) - .6) < 1e-9 && bucket.GetContent(receiverPos)!.Collectible.Code.Path == "honeyportion",
            "Incompatible liquid stopped the spout or was mixed with water.");
        receiver.Inventory[0].Itemstack = null;
        // Real held wooden bucket filling and empty-bucket recovery retain metadata.
        slot.Itemstack = new ItemStack(bucket);
        bucket.SetContent(slot.Itemstack, new ItemStack(saltItem, 100));
        metadata.GetMethod("Write")!.Invoke(null, [bucket.GetContent(slot.Itemstack), raw]);
        Require((bool)Call(source, "TransferHeld", player, 0, slot, bucket)! && Math.Abs(Volume(sourceState) - 1.6) < 1e-9
            && bucket.GetContent(slot.Itemstack) is null, "Native held bucket could not fill basin.");
        Require((bool)Call(source, "TransferHeld", player, 0, slot, bucket)! && Volume(sourceState) == 0
            && bucket.GetContent(slot.Itemstack)!.StackSize == 160, "Recovering retained water lost volume.");
        Require(Quality(bucket.GetContent(slot.Itemstack)!).SaltGramsPerL == 35, "Held recovery lost salt metadata.");
        var barrelBlock = new BlockBarrel { Code = new("game:barrel"), BlockId = 61 };
        typeof(CollectibleObject).GetField("api", Hidden)!.SetValue(barrelBlock, api);
        var barrel = new BlockEntityBarrel { Api = api, Block = barrelBlock, Pos = receiverPos, Sealed = true };
        barrel.Inventory.Api = api; native[receiverPos] = barrel;
        Set(sourceState, "Water", raw);
        Require((double)Call(source, "TransferTo", 0, receiverPos, 1, .25d)! == .25 && barrelBlock.GetContent(receiverPos) is null,
            "Sealed barrel accepted automatic water or blocked wasted flow.");
        barrel.Sealed = false;
        Require((double)Call(source, "TransferTo", 0, receiverPos, 1, .25d)! == .25
            && barrelBlock.GetContent(receiverPos)!.StackSize == 25, "Open native barrel did not receive water in its liquid slot.");
        // Follow the actual server tick path into basins at either half of the
        // block below the outlet, including a vacant lower half under a pipe.
        for (var d = 0; d < 4; d++)
            foreach (var sourceHalf in new[] { 0, 1 })
                foreach (var receivingHalf in new[] { 0, 1 })
                {
                    cells.Clear(); native.Clear();
                    Require(Place("Drain", pos, sourceHalf, d), "Diversion chain source placement failed.");
                    var delta = d switch { 0 => (0, -1), 1 => (1, 0), 2 => (0, 1), _ => (-1, 0) };
                    var below = pos.AddCopy(delta.Item1, -1, delta.Item2);
                    Require(Place("Drain", below, receivingHalf, (d + 1) & 3), "Turning receiving basin placement failed.");
                    var upstream = cells[pos]; var upstreamState = State(upstream, sourceHalf);
                    var downstream = cells[below]; var downstreamState = State(downstream, receivingHalf);
                    Set(upstreamState, "Water", raw);
                    Call(upstream, "Tick", 1f);
                    Require(Math.Abs(Volume(upstreamState) - .9) < 1e-9 && Math.Abs(Volume(downstreamState) - .1) < 1e-9,
                        "Pipe failed to find a lower turning basin in direction/halves " + d + "/" + sourceHalf + "/" + receivingHalf);
                    Require((WaterBatch)Get(downstreamState, "Water")! == raw with { VolumeL = .1 }, "Unlined routing changed water metadata.");
                    Set(downstreamState, "Water", raw with { VolumeL = 10 });
                    Call(upstream, "Tick", 1f);
                    Require(Math.Abs(Volume(upstreamState) - .8) < 1e-9 && Volume(downstreamState) == 10,
                        "Full basin stopped the spout or accepted overflow.");
                    Set(downstreamState, "Water", null);
                    deniedPos = below; Call(upstream, "Tick", 1f); deniedPos = null;
                    Require(Volume(downstreamState) == 0, "Falling water bypassed receiver claim.");
                    unloadedPos = below; Call(upstream, "Tick", 1f); unloadedPos = null;
                    Require(Volume(downstreamState) == 0, "Falling water entered unloaded receiver.");
                    // A pot directly under the outlet is still supported.
                    if (sourceHalf == 1 && receivingHalf == 0)
                    {
                        var immediate = pos.AddCopy(delta.Item1, 0, delta.Item2);
                        Require(Place("Drain", immediate, 0, (d + 1) & 3), "Immediate half-basin placement failed.");
                        Set(State(cells[immediate], 0), "Water", raw with { VolumeL = 10 });
                        Call(upstream, "Tick", 1f);
                        Require(Volume(downstreamState) == 0, "Flow skipped a full immediate basin.");
                    }
                }
        // Gravity inflow must reach either cell of the same bath reservoir.
        for (var d = 0; d < 4; d++)
            foreach (var sourceHalf in new[] { 0, 1 })
                foreach (var hitPart in new[] { false, true })
                for (var bathDirection = 0; bathDirection < 4; bathDirection++)
                {
                    cells.Clear(); native.Clear(); baths.Clear();
                    Require(Place("Drain", pos, sourceHalf, d), "Bath source placement failed.");
                    var delta = d switch { 0 => (0, -1), 1 => (1, 0), 2 => (0, 1), _ => (-1, 0) };
                    var landing = pos.AddCopy(delta.Item1, -1, delta.Item2);
                    var offset = bathDirection switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                    var bathPos = hitPart ? landing.AddCopy(-offset.Item1, 0, -offset.Item2) : landing.Copy();
                    var partPos = bathPos.AddCopy(offset.Item1, 0, offset.Item2);
                    var bath = new BlockEntityBathtub { Api = api, Pos = bathPos,
                        Block = new BlockBathtub { Code = new("vsslssmod:bathtub"), BlockId = 70 } };
                    var part = new BlockEntityBathtub { Api = api, Pos = partPos,
                        Block = new BlockBathtub { Code = new("vsslssmod:bathtub-part"), BlockId = 71 } };
                    typeof(BlockEntityBathtub).GetMethod("SetDirection", Hidden)!.Invoke(bath, [bathDirection]);
                    typeof(BlockEntityBathtub).GetMethod("SetDirection", Hidden)!.Invoke(part, [bathDirection]);
                    baths[bathPos] = bath; baths[partPos] = part;
                    var upstream = cells[pos]; var state = State(upstream, sourceHalf);
                    Set(state, "Water", raw);
                    Call(upstream, "Tick", 1f);
                    Require(Math.Abs(bath.WaterLitres - .1) < 1e-9 && Math.Abs(Volume(state) - .9) < 1e-9
                        && part.WaterLitres == 0, "Bath gravity delivery lost/duplicated water or wrote to its part.");
                    var waterField = typeof(BlockEntityBathtub).GetField("water", Hidden)!;
                    var received = (WaterBatch)waterField.GetValue(bath)!;
                    Require(received == raw with { VolumeL = .1 }, "Bath inflow changed quality metadata.");
                    var visual = new TreeAttribute(); part.ToTreeAttributes(visual);
                    Require(Math.Abs(visual.GetDouble("visualWaterVolume") - .1) < 1e-9, "Bath part visual did not synchronize.");
                    var existing = raw with { VolumeL = 199.95, TemperatureC = 10, SaltGramsPerL = 0, Turbidity = .1,
                        Source = "pond", PathogensPerL = new Dictionary<string, double> { ["shigella"] = .5 } };
                    waterField.SetValue(bath, existing);
                    Call(upstream, "Tick", 1f);
                    var mixed = (WaterBatch)waterField.GetValue(bath)!;
                    var expected = existing.Mix(raw with { VolumeL = .05 });
                    Require(Math.Abs(bath.WaterLitres - 200) < 1e-9 && Math.Abs(Volume(state) - .8) < 1e-9
                        && Math.Abs(mixed.TemperatureC - expected.TemperatureC) < 1e-9
                        && Math.Abs(mixed.SaltGramsPerL - expected.SaltGramsPerL) < 1e-9
                        && Math.Abs(mixed.Turbidity - expected.Turbidity) < 1e-9
                        && Math.Abs(mixed.TotalEntericHazardPerL - expected.TotalEntericHazardPerL) < 1e-9,
                        "Nearly full bath failed capacity, mixing or overflow accounting.");
                    Call(upstream, "Tick", 1f);
                    Require(bath.WaterLitres == 200 && Math.Abs(Volume(state) - .7) < 1e-9,
                        "Full bath blocked open flow or overflowed capacity.");
                    foreach (var protectedCell in new[] { bathPos, partPos })
                    {
                        deniedPos = protectedCell; Call(upstream, "Tick", 1f); deniedPos = null;
                        Require(Math.Abs(Volume(state) - .7) < 1e-9, "Bath automation bypassed a cell claim.");
                        unloadedPos = protectedCell; Call(upstream, "Tick", 1f); unloadedPos = null;
                        Require(Math.Abs(Volume(state) - .7) < 1e-9, "Bath automation crossed an unloaded cell.");
                    }
                    var savedBath = new TreeAttribute(); bath.ToTreeAttributes(savedBath);
                    var loadedBath = new BlockEntityBathtub { Api = api, Block = bath.Block, Pos = bathPos };
                    loadedBath.FromTreeAttributes(savedBath, world);
                    var loadedWater = (WaterBatch)waterField.GetValue(loadedBath)!;
                    Require(loadedBath.WaterLitres == 200 && loadedWater.Source == mixed.Source
                        && loadedWater.SaltGramsPerL == mixed.SaltGramsPerL
                        && loadedWater.PathogensPerL.OrderBy(p => p.Key).SequenceEqual(mixed.PathogensPerL.OrderBy(p => p.Key)),
                        "Bath gravity water did not survive persistence.");
                }
        baths.Clear();
        Console.WriteLine("PASS bath gravity inflow across four directions/both heights/both cells, quality mixing, capacity, overflow, claims/loading, visual sync and persistence");
        // A drain directly above a bathtub is valid without the usual side
        // pipe. Exercise both drain halves and both bathtub cells through the
        // actual server tick path.
        for (var sourceHalf = 0; sourceHalf < 2; sourceHalf++)
            foreach (var hitPart in new[] { false, true })
                for (var bathDirection = 0; bathDirection < 4; bathDirection++)
            {
                cells.Clear(); native.Clear(); baths.Clear();
                var direct = new BlockEntityWaterTreatment { Api = api, Block = cell, Pos = pos };
                var slotType = typeof(BlockEntityWaterTreatment).Assembly.GetType("Seraph.World.TreatmentSlot", true)!;
                var directSlot = Activator.CreateInstance(slotType)!;
                Set(directSlot, "Kind", Enum.Parse(kindType, "Drain"));
                Set(directSlot, "Direction", 0);
                Set(directSlot, "OwnerUid", player.PlayerUID);
                Set(directSlot, "Water", raw);
                var directSlots = Array.CreateInstance(slotType, 2);
                directSlots.SetValue(directSlot, sourceHalf);
                directSlots.SetValue(Activator.CreateInstance(slotType)!, 1 - sourceHalf);
                typeof(BlockEntityWaterTreatment).GetProperty("Slots", Hidden)!.SetValue(direct, directSlots);
                cells[pos] = direct;
                var landing = pos.AddCopy(0, -1, 0);
                var offset = bathDirection switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                    var bathPos = hitPart ? landing.AddCopy(-offset.Item1, 0, -offset.Item2) : landing.Copy();
                var partPos = bathPos.AddCopy(offset.Item1, 0, offset.Item2);
                var bath = new BlockEntityBathtub { Api = api, Pos = bathPos,
                    Block = new BlockBathtub { Code = new("vsslssmod:bathtub"), BlockId = 70 } };
                var part = new BlockEntityBathtub { Api = api, Pos = partPos,
                    Block = new BlockBathtub { Code = new("vsslssmod:bathtub-part"), BlockId = 71 } };
                typeof(BlockEntityBathtub).GetMethod("SetDirection", Hidden)!.Invoke(bath, [bathDirection]);
                    typeof(BlockEntityBathtub).GetMethod("SetDirection", Hidden)!.Invoke(part, [bathDirection]);
                    baths[bathPos] = bath; baths[partPos] = part;
                Call(direct, "Tick", 1f);
                Require(Math.Abs(bath.WaterLitres - .1) < 1e-9
                    && Math.Abs(Volume(State(direct, sourceHalf)) - .9) < 1e-9
                    && part.WaterLitres == 0,
                    "Drain without a side pipe did not fill the bathtub below (half/part " + sourceHalf + "/" + hitPart + ").");
            }
        baths.Clear();
        Console.WriteLine("PASS direct drain-to-bathtub inflow without side pipe across both heights and cells");
        // Three pots may change direction independently and deliver to a native
        // bucket without losing or progressively purifying the same water.
        cells.Clear(); native.Clear();
        var bend = pos.AddCopy(0, -1, -1);
        var last = bend.AddCopy(1, -1, 0);
        var end = last.AddCopy(0, -1, 1);
        Require(Place("Drain", pos, 1, 0) && Place("Drain", bend, 0, 1) && Place("Drain", last, 0, 2), "Three-pot chain placement failed.");
        var endBucket = new BlockEntityBucket { Block = bucket, Pos = end, Api = api };
        endBucket.Inventory.Api = api; native[end] = endBucket;
        Set(State(cells[pos], 1), "Water", raw);
        for (var i = 0; i < 30; i++)
        {
            Call(cells[pos], "Tick", 1f); Call(cells[bend], "Tick", 1f); Call(cells[last], "Tick", 1f);
            var heldVolume = Volume(State(cells[pos], 1)) + Volume(State(cells[bend], 0)) + Volume(State(cells[last], 0));
            Require(Math.Abs(heldVolume + (bucket.GetContent(end)?.StackSize ?? 0) / 100d - 1) < 1e-9,
                "Three-pot chain lost or duplicated water.");
        }
        Require(bucket.GetContent(end)?.StackSize == 100, "Turning chain failed to deliver the full batch to the bucket.");
        var delivered = Quality(bucket.GetContent(end)!);
        Require(delivered.Source == raw.Source && delivered.Unverified == raw.Unverified
            && new[] { delivered.VolumeL - raw.VolumeL, delivered.TemperatureC - raw.TemperatureC,
                delivered.SaltGramsPerL - raw.SaltGramsPerL, delivered.EntericHazardPerL - raw.EntericHazardPerL,
                delivered.ChemicalHazardPerL - raw.ChemicalHazardPerL, delivered.Turbidity - raw.Turbidity }
                .All(delta => Math.Abs(delta) < 1e-9), "Turning chain changed the delivered water quality.");
        // Real listeners do not run at exactly one second. Sub-portion transfers
        // must eventually drain into the native 100 portions/L container.
        Set(State(cells[pos], 1), "Water", raw);
        endBucket.Inventory[0].Itemstack = null;
        var intervals = new[] { .973f, 1.013f, 1.071f };
        for (var i = 0; i < 100; i++)
        {
            foreach (var (p, h) in new[] { (pos, 1), (bend, 0), (last, 0) }) Call(cells[p], "Tick", intervals[i % 3]);
            var retained = Volume(State(cells[pos], 1)) + Volume(State(cells[bend], 0)) + Volume(State(cells[last], 0));
            Require(Math.Abs(retained + (bucket.GetContent(end)?.StackSize ?? 0) / 100d - 1) < 1e-9,
                "Variable tick intervals lost or duplicated water.");
        }
        Require(bucket.GetContent(end)?.StackSize == 100 && new[] { (pos, 1), (bend, 0), (last, 0) }
            .All(p => Volume(State(cells[p.Item1], p.Item2)) == 0), "Variable tick intervals left undrainable water in a pot.");
        // A cloth pot uses its own slower rate and only consumes actual output.
        Set(State(cells[pos], 1), "Water", raw);
        Require(Insert(State(cells[pos], 1), linen, 1, 1), "Cloth diversion setup failed.");
        Call(cells[pos], "Tick", 1f);
        Require(Math.Abs(Volume(State(cells[bend], 0)) - .05) < 1e-9
            && Math.Abs((double)Get(State(cells[pos], 1), "UsedL")! - .05) < 1e-9, "Cloth diversion rate/media use is wrong.");
        // No water may jump an obstructing pipe or a full block, or fall farther
        // than the block immediately below the outlet.
        cells.Clear(); native.Clear();
        Require(Place("Drain", pos, 0, 0), "Obstruction test source placement failed.");
        var obstructed = pos.AddCopy(0, -1, -1);
        Require(Place("Drain", obstructed, 0, 1) && Place("Drain", obstructed.AddCopy(-1, 0, 0), 1, 1), "Obstructing pipe setup failed.");
        Set(State(cells[pos], 0), "Water", raw); Call(cells[pos], "Tick", 1f);
        Require(Volume(State(cells[obstructed], 0)) == 0, "Water passed through an obstructing pipe.");
        cells.Clear();
        Require(Place("Drain", pos, 0, 0), "Solid obstruction source placement failed.");
        var solid = pos.AddCopy(0, -1, -1);
        native[solid] = new BlockEntityBucket { Block = new Block { BlockId = 99, Code = new("game:stone") }, Pos = solid, Api = api };
        Set(State(cells[pos], 0), "Water", raw); Call(cells[pos], "Tick", 1f);
        Require(Math.Abs(Volume(State(cells[pos], 0)) - .9) < 1e-9 && (bool)Get(State(cells[pos], 0), "Flowing")!,
            "Solid surface stopped the open spout instead of wasting water.");
        cells.Clear(); native.Clear();
        Require(Place("Drain", pos, 0, 0) && Place("Drain", pos.AddCopy(0, -2, -1), 1, 1), "Out-of-range setup failed.");
        Set(State(cells[pos], 0), "Water", raw); Call(cells[pos], "Tick", 1f);
        Require(Math.Abs(Volume(State(cells[pos], 0)) - .9) < 1e-9 && Volume(State(cells[pos.AddCopy(0, -2, -1)], 1)) == 0,
            "Open spout stopped wasting or delivered to a receiver outside the bounded search.");
        cells.Clear(); native.Clear();
        Require(Place("Drain", pos, 0, 0), "Open waste source placement failed.");
        var wasting = cells[pos]; var wasteState = State(wasting, 0);
        Set(wasteState, "Water", raw);
        var beforeWaste = particles;
        for (var i = 0; i < 12; i++) Call(wasting, "Tick", 1f);
        Require(Volume(wasteState) == 0 && !(bool)Get(wasteState, "Flowing")! && particles == beforeWaste + 10,
            "Uncaught water failed to drain completely, emit drops or stop after emptying.");
        Set(wasteState, "Water", raw); allowed = false; Call(wasting, "Tick", 1f); allowed = true;
        Require(Volume(wasteState) == 1, "Waste bypassed source claim protection.");
        unloadedPos = pos.AddCopy(0, -1, -1); Call(wasting, "Tick", 1f); unloadedPos = null;
        Require(Volume(wasteState) == 1, "Waste ran across an unloaded outlet.");
        Console.WriteLine("PASS diversion chains in four directions/both halves, one-block falls, independent turns, native bucket delivery, obstruction/claims/loading and conserved quality");
        // Native engine offsets placement above a slab; reuse its vacant upper half.
        cells.Clear(); native.Clear();
        Require(Place("Filter", pos, 0, 0), "Half merge setup failed.");
        string failure = "";
        Require(filter.TryPlaceBlock(world, player, new ItemStack(filter), new BlockSelection { Position = pos.UpCopy(),
            Face = BlockFacing.UP, HitPosition = new Vec3d(.5, .5, .5), DidOffset = true }, ref failure)
            && cells.Count == 1 && Get(State(cells[pos], 1), "Kind")!.ToString() == "Filter", "Native slab-style offset did not merge the second half.");
        foreach (var broken in new[] { "[{\"Kind\":1,\"OwnerUid\":null},{}]", "[{\"Kind\":1,\"Gravel\":{\"Code\":null,\"Class\":1,\"Quantity\":1}},{}]" })
        {
            var tree = new TreeAttribute(); tree.SetString("treatmentSlots", broken);
            restored.FromTreeAttributes(tree, world);
            Require(Get(State(restored, 0), "Kind")!.ToString() == "Empty", "Malformed null values crashed or entered persistence.");
        }
        Console.WriteLine("PASS actual native bucket receiving/capacity/metadata, held filling/recovery, flow particles and engine half-placement merge");
        foreach (var code in new[] { "filterpot", "drainpot", "watertreatment" })
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/blocktypes", code + ".json")));
            foreach (var texture in json.RootElement.GetProperty("textures").EnumerateObject())
            {
                var relative = texture.Value.GetProperty("base").GetString()!.Replace("game:", "");
                Require(File.Exists(Path.Combine(gamePath, "assets/survival/textures", relative + ".png")), "Missing native texture " + relative);
            }
        }
        Console.WriteLine("PASS half-height fixture stacking, all four directions, linked-cell teardown, claims/loading, media order/life, quality conservation and persistence");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}


