using System.Text.Json;
using System.Buffers.Binary;
using Seraph.Core.Framework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.World;

public sealed class BlockEntityWaterTreatment : BlockEntity
{
    private bool releaseFormatRejected;

    internal TreatmentSlot[] Slots { get; private set; } = [new(), new()];
    private bool removing;
    private double? settledGameSeconds;
    private double flowLandingY;
    private const int FlowPacket = 1040;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
        {
            settledGameSeconds = WorldCalendarTime.Seconds(api.World);
            RegisterGameTickListener(Tick, 1000);
        }
    }

    internal void Changed()
    {
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    internal (BlockEntityWaterTreatment Entity, int Half)? Master(int half)
    {
        if (half is < 0 or > 1) return null;
        var slot = Slots[half];
        if (slot.Kind is TreatmentKind.Filter or TreatmentKind.Drain) return (this, half);
        if (slot.Kind != TreatmentKind.Pipe || Api is null) return null;
        var pos = BlockWaterTreatment.BasinPos(Pos, slot.Direction);
        if (Api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityWaterTreatment e
            && e.Slots[half].Kind == TreatmentKind.Drain && e.Slots[half].Direction == slot.Direction) return (e, half);
        return null;
    }

    internal bool Interact(IPlayer player, int half)
    {
        if (Master(half) is not { } master) return true;
        if (!ReferenceEquals(master.Entity, this)) return master.Entity.Interact(player, half);
        if (Api?.Side != EnumAppSide.Server) return true;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return true;
        var slot = Slots[half];
        var held = player.InventoryManager.ActiveHotbarSlot;
        if (held.Itemstack is { } stack && slot.TryInsert(stack, out var quantity))
        {
            if (player.WorldData.CurrentGameMode != EnumGameMode.Creative) held.TakeOut(quantity);
            held.MarkDirty(); Changed(); return true;
        }
        if (held.Itemstack?.Collectible is BlockLiquidContainerBase container)
        {
            if (TransferHeld(player, half, held, container)) { Changed(); return true; }
        }
        else if (held.Empty && player.Entity.Controls.ShiftKey && slot.Volume <= .00001)
        {
            // Unused layers are returned. Used media is discarded, preventing free renewal.
            foreach (var material in Materials(slot))
                if (slot.UsedL <= .00001 && material.Resolve(Api.World) is { } returned)
                    if (!player.InventoryManager.TryGiveItemstack(returned)) Api.World.SpawnItemEntity(returned, Pos.ToVec3d().Add(.5, .5, .5));
            slot.Gravel = null; slot.Charcoal = null; slot.Cloth = null; slot.UsedL = 0;
            Changed(); return true;
        }
        return true;
    }

    internal bool TransferHeld(IPlayer player, int half, ItemSlot held, BlockLiquidContainerBase container)
    {
        var slot = Slots[half];
        var content = container.GetContent(held.Itemstack!);
        if (WaterQuality.IsWater(content))
        {
            var config = Api.ModLoader.GetModSystem<SeraphModSystem>().Server?.InteractionConfig;
            if (config is null) return false;
            var incoming = WaterQuality.ReadLiquidAt(Api.World, content, WaterQuality.Ambient(Api.World, Pos), config);
            var props = BlockLiquidContainerBase.GetContainableProps(content);
            if (incoming is null || props is not { ItemsPerLitre: > 0 }) return false;
            var count = (int)Math.Floor(Math.Min(incoming.VolumeL, WaterFiltration.ReservoirL - slot.Volume) * props.ItemsPerLitre + 1e-6);
            if (count <= 0) return false;
            var moved = container.SplitStackAndPerformAction(player.Entity, held,
                stack => container.TryTakeContent(stack, count)?.StackSize ?? 0);
            if (moved <= 0) return false;
            slot.Accept(incoming with { VolumeL = moved / (double)props.ItemsPerLitre });
            held.MarkDirty(); return true;
        }
        // Recover the retained water into an empty bucket; no treatment is applied a second time.
        if (content is not null || slot.Water is null || !container.IsTopOpened) return false;
        var liquid = Liquid(slot.Water);
        if (liquid is null) return false;
        var units = BlockLiquidContainerBase.GetContainableProps(liquid)!.ItemsPerLitre;
        var put = container.SplitStackAndPerformAction(player.Entity, held,
            stack => container.TryPutLiquid(stack, liquid, (float)slot.Volume));
        if (put <= 0) return false;
        var remaining = slot.Volume - put / (double)units;
        slot.Water = remaining <= .00001 ? null : slot.Water with { VolumeL = remaining };
        held.MarkDirty(); return true;
    }

    private ItemStack? Liquid(WaterBatch batch)
    {
        var item = Api.World.GetItem(new AssetLocation("game", batch.SaltGramsPerL > 5 ? "saltwaterportion" : "waterportion"));
        if (item is null) return null;
        var stack = new ItemStack(item);
        var props = BlockLiquidContainerBase.GetContainableProps(stack);
        if (props is not { ItemsPerLitre: > 0 }) return null;
        stack.StackSize = (int)Math.Floor(batch.VolumeL * props.ItemsPerLitre + 1e-6);
        if (stack.StackSize <= 0) return null;
        WaterMetadata.WriteAt(Api.World, stack, batch);
        return stack;
    }

    private void Tick(float dt)
    {
        var now = WorldCalendarTime.Seconds(Api.World);
        var elapsed = Math.Min(60, Math.Max(0, now - (settledGameSeconds ?? now)));
        settledGameSeconds = (settledGameSeconds ?? now) + elapsed;
        dt = (float)(elapsed / 30); // Preserve the former normal-calendar flow, including sleep acceleration.
        for (var half = 0; half < 2; half++)
        {
            var slot = Slots[half];
            if (slot.Kind is TreatmentKind.Drain or TreatmentKind.Pipe)
            {
                var linked = slot.Kind == TreatmentKind.Drain ? BlockWaterTreatment.PipePos(Pos, slot.Direction)
                    : BlockWaterTreatment.BasinPos(Pos, slot.Direction);
                if (Api.World.BlockAccessor.GetChunkAtBlockPos(linked) is not null
                    && (Api.World.BlockAccessor.GetBlockEntity(linked) is not BlockEntityWaterTreatment partner
                        || partner.Slots[half].Direction != slot.Direction
                        || partner.Slots[half].Kind != (slot.Kind == TreatmentKind.Drain ? TreatmentKind.Pipe : TreatmentKind.Drain)))
                {
                    // A drain can intentionally omit its side pipe when a
                    // bathtub occupies the block immediately below it. Keep
                    // the slot and continue with its normal transfer below.
                    if (slot.Kind != TreatmentKind.Drain || HasPipe(half) || !DirectBathBelow())
                    {
                        // Reconcile a linked cell removed while this chunk was unloaded.
                        Remove(half, slot.Kind == TreatmentKind.Drain); continue;
                    }
                }
            }
            if (slot.Kind is not (TreatmentKind.Filter or TreatmentKind.Drain)) continue;
            var previousFlow = slot.Flowing;
            slot.Flowing = false;
            var amount = WaterFiltration.TransferVolume(slot.Volume, WaterFiltration.ReservoirL, dt,
                slot.RemainingL, slot.Kind == TreatmentKind.Filter || slot.Cloth is not null, slot.FlowLPerSecond);
            var hasPipe = slot.Kind == TreatmentKind.Drain && HasPipe(half);
            var directBath = slot.Kind == TreatmentKind.Drain && !hasPipe && DirectBathBelow();
            if (slot.Ready && amount > .00001 && (slot.Kind != TreatmentKind.Drain || hasPipe || directBath))
            {
                var outlet = slot.Kind == TreatmentKind.Drain
                    ? hasPipe ? BlockWaterTreatment.PipePos(Pos, slot.Direction) : Pos.AddCopy(0, -1, 0)
                    : Pos;
                var target = directBath ? outlet : half == 1 ? outlet : outlet.AddCopy(0, -1, 0);
                var targetHalf = half == 1 ? 0 : 1;
                var moved = TransferTo(half, target, targetHalf, amount);
                if (moved > .00001)
                {
                    slot.Consume(moved); slot.Flowing = true;
                    if (slot.Kind == TreatmentKind.Drain) SpawnFlow(half);
                    Changed();
                }
            }
            if (previousFlow != slot.Flowing) Changed();
        }
    }

    private bool HasPipe(int half)
    {
        var s = Slots[half];
        var target = BlockWaterTreatment.PipePos(Pos, s.Direction);
        return Api.World.BlockAccessor.GetChunkAtBlockPos(target) is not null
            && Api.World.BlockAccessor.GetBlockEntity(target) is BlockEntityWaterTreatment e
            && e.Slots[half].Kind == TreatmentKind.Pipe && e.Slots[half].Direction == s.Direction;
    }

    private bool DirectBathBelow()
    {
        var target = Pos.AddCopy(0, -1, 0);
        if (Api.World.BlockAccessor.GetChunkAtBlockPos(target) is null) return false;
        // GetMaster resolves the companion cell to the two-cell tub's owner.
        // Keep the transfer coordinate directly below the drain so detection and
        // delivery cannot disagree about where the water should land.
        return BlockBathtub.GetMaster(Api.World, target) is not null;
    }

    internal double TransferTo(int half, BlockPos target, int targetHalf, double amount)
    {
        var s = Slots[half];
        if (s.Water is null || !s.Ready || amount <= 0 || Api.World.BlockAccessor.GetChunkAtBlockPos(target) is null) return 0;
        var owner = Api.World.PlayerByUid(s.OwnerUid);
        // Pause unattended automation if ownership/access cannot be verified.
        if (owner is null || !Api.World.Claims.TryAccess(owner, Pos, EnumBlockAccessFlags.Use)
            || !Api.World.Claims.TryAccess(owner, target, EnumBlockAccessFlags.Use)) return 0;
        amount = Math.Min(amount, Math.Min(s.Volume, s.Cloth is null ? amount : s.RemainingL));
        // Visit the half immediately below the outlet first, then fall through
        // empty halves to the bottom of the block below the pipe. Never search
        // sideways, skip an obstructing fixture, or recurse through a network.
        var bottomY = Pos.Y - 1;
        while (target.Y >= bottomY)
        {
            if (Api.World.BlockAccessor.GetChunkAtBlockPos(target) is null
                || !Api.World.Claims.TryAccess(owner, target, EnumBlockAccessFlags.Use)) return 0;
            if (Api.World.BlockAccessor.GetBlockEntity(target) is BlockEntityWaterTreatment other)
            {
                var receiving = other.Slots[targetHalf];
                if (receiving.Kind is TreatmentKind.Drain or TreatmentKind.Filter)
                {
                    if (ReferenceEquals(other, this) && targetHalf == half
                        || s.Kind == TreatmentKind.Filter && receiving.Kind != TreatmentKind.Drain) return 0;
                    var accepted = receiving.Accept(s.Product(amount));
                    flowLandingY = target.InternalY + targetHalf * .5 + WaterHeight(receiving) / 16;
                    if (accepted > 0) other.Changed();
                    // An open spout keeps draining when the receiving basin is full.
                    return s.Kind == TreatmentKind.Drain ? amount : accepted;
                }
                if (receiving.Kind != TreatmentKind.Empty)
                    return Spill(target.InternalY + (targetHalf + 1) * .5);
            }
            else
            {
                var block = Api.World.BlockAccessor.GetBlock(target);
                if (s.Kind == TreatmentKind.Drain && block is BlockBathtub
                    && BlockBathtub.GetMaster(Api.World, target) is { } bath)
                {
                    // Bath cells share a reservoir; require access to the full footprint
                    // just like manual filling, including across chunk boundaries.
                    var parts = bath.Block?.Code?.Path == "copperbathtub"
                        ? new[] { FixtureOrientation.BathOffset(bath.Pos, bath.Direction, -1), FixtureOrientation.BathOffset(bath.Pos, bath.Direction, 1) }
                        : new[] { FixtureOrientation.BathPartner(bath.Pos, bath.Direction, false) };
                    if (Api.World.BlockAccessor.GetChunkAtBlockPos(bath.Pos) is null
                        || parts.Any(part => Api.World.BlockAccessor.GetChunkAtBlockPos(part) is null
                            || !Api.World.Claims.TryAccess(owner, part, EnumBlockAccessFlags.Use))
                        || !Api.World.Claims.TryAccess(owner, bath.Pos, EnumBlockAccessFlags.Use)) return 0;
                    bath.AcceptWater(s.Product(amount));
                    flowLandingY = bath.Pos.InternalY + .125 + .625 * bath.WaterLitres / bath.ReservoirCapacity;
                    // Like other open basins, a full bath spills the excess.
                    return amount;
                }
                if (s.Kind == TreatmentKind.Drain && block is BlockLiquidContainerBase container)
                {
                    flowLandingY = target.InternalY + .65;
                    return TransferToContainer(s, target, container, amount);
                }
                if (block.BlockId != 0) return Spill(target.InternalY + 1);
            }
            // Layered filtration requires a basin touching its underside.
            if (s.Kind == TreatmentKind.Filter) return 0;
            if (targetHalf == 1) targetHalf = 0;
            else { target = target.DownCopy(); targetHalf = 1; }
        }
        return Spill(Pos.InternalY - 1);

        double Spill(double landingY)
        {
            if (s.Kind != TreatmentKind.Drain) return 0;
            flowLandingY = Math.Min(Outlet(half).Y - .01, landingY);
            return amount;
        }
    }

    private double TransferToContainer(TreatmentSlot s, BlockPos containerPos, BlockLiquidContainerBase container, double amount)
    {
        var barrel = Api.World.BlockAccessor.GetBlockEntity(containerPos) as BlockEntityBarrel;
        // Vanilla barrels expose their liquid slot when unsealed; their generic
        // IsTopOpened flag is not populated consistently by all barrel states.
        // Keep other containers on the native open-top check and always reject
        // sealed barrels.
        if (barrel is { Sealed: true } || !container.IsTopOpened && barrel is null) return amount;
        var current = container.GetContent(containerPos);
        if (current is not null && !WaterQuality.IsWater(current)) return amount;
        var liquid = Liquid(s.Product(amount));
        if (liquid is null) return 0;
        var units = BlockLiquidContainerBase.GetContainableProps(liquid)!.ItemsPerLitre;
        var free = Math.Max(0, container.CapacityLitres - (current?.StackSize ?? 0) / (double)units);
        var moved = container.TryPutLiquid(containerPos, liquid, (float)amount);
        // Overflow is lost, but fractional native liquid portions remain in the
        // pot until they accumulate into a transferable portion, avoiding loss
        // in correctly assembled chains solely due to tick timing.
        return amount > free + .000001 ? amount : Math.Clamp(moved / (double)units, 0, amount);
    }

    internal Vec3d Outlet(int half)
    {
        // The exported pipe ends 9/16 of a block north of the basin anchor.
        var p = new Vec3d(.5, half * .5 + 1d/18, -9d/16);
        for (var i = 0; i < Slots[half].Direction; i++) p = new Vec3d(1 - p.Z, p.Y, p.X);
        return p.Add(Pos.X, Pos.InternalY, Pos.Z);
    }
    private void SpawnFlow(int half)
    {
        var p = Outlet(half);
        // Treatment cells reserve half/full block collision boxes. A real falling
        // drop must pass through those boxes and end at the receiving surface.
        // Use a known speed and distance so particles cannot leak through its floor.
        var distance = Math.Max(.01, p.Y - flowLandingY);
        // SimpleParticleProperties does not serialize WithTerrainCollision in
        // VS 1.22.7. Send the fall geometry, then create the particles locally.
        if (Api is not ICoreServerAPI server) return;
        var data = new byte[6];
        data[0] = (byte)half; data[1] = (byte)Slots[half].Direction;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(2), (float)distance);
        server.Network.BroadcastBlockEntityPacket(Pos, FlowPacket, data);
    }

    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid != FlowPacket) { base.OnReceivedServerPacket(packetid, data); return; }
        if (Api is not ICoreClientAPI || data.Length != 6 || data[0] > 1 || data[1] > 3) return;
        var half = data[0];
        var distance = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(2));
        if (!float.IsFinite(distance) || distance is < .001f or > 2 || Slots[half].Kind != TreatmentKind.Drain
            || Slots[half].Direction != data[1]) return;
        var p = Outlet(half);
        Api.World.SpawnParticles(new SimpleParticleProperties(8, 8, ColorUtil.ToRgba(180, 65, 135, 205), p,
            p.AddCopy(.015, 0, .015), new Vec3f(0, -1.5f, 0), new Vec3f(0, -1.5f, 0),
            (float)(distance / 1.5), 0, .06f, .06f, EnumParticleModel.Quad)
        { WithTerrainCollision = false, ShouldDieInLiquid = true });
    }

    internal string Info(int half)
    {
        var s = Slots[half];
        var result = Lang.Get("vsslssmod:filter-water", s.Volume.ToString("0.0"), WaterFiltration.ReservoirL.ToString("0"));
        if (s.NextMaterial is { } material) result += "\n" + LocalizedText.Get("vsslssmod:filter-add-" + material);
        if (s.Cloth is not null) result += "\n" + LocalizedText.Get("vsslssmod:filter-life", s.RemainingL.ToString("0.0"));
        result += "\n" + LocalizedText.Get("vsslssmod:filter-flow", (s.FlowLPerSecond * 60).ToString("0.#"));
        result += "\n" + Lang.Get(s.Kind == TreatmentKind.Filter ? "vsslssmod:filter-needs-drain" : "vsslssmod:filter-drain-help");
        if (s.Water is { } w) result += "\n" + string.Join("\n", WaterInfoText.Lines(w,
            (key, args) => LocalizedText.Get("vsslssmod:" + key, args), includeVolume: false, includeWaste: false));
        return result;
    }

    private static IEnumerable<FilterMaterial> Materials(TreatmentSlot s)
        => new[] { s.Gravel, s.Charcoal, s.Cloth }.OfType<FilterMaterial>();
    private IEnumerable<ItemStack> Drops(int half)
    {
        var s = Slots[half];
        if (s.Kind is not (TreatmentKind.Filter or TreatmentKind.Drain)) yield break;
        yield return new ItemStack(Api.World.GetBlock(new AssetLocation("vsslssmod:" + (s.Kind == TreatmentKind.Filter ? "filterpot" : "drainpot"))));
        if (s.UsedL <= .00001)
            foreach (var material in Materials(s))
                if (material.Resolve(Api.World) is { } stack) yield return stack;
    }
    internal ItemStack[] AllDrops() => Enumerable.Range(0, 2).SelectMany(Drops).ToArray();

    internal void Remove(int half, bool drops)
    {
        if (removing) return;
        removing = true;
        try
        {
            var s = Slots[half];
            if (drops)
                foreach (var stack in Drops(half)) Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(.5, half * .5, .5));
            Slots[half] = new();
            var linked = s.Kind == TreatmentKind.Drain ? BlockWaterTreatment.PipePos(Pos, s.Direction)
                : s.Kind == TreatmentKind.Pipe ? BlockWaterTreatment.BasinPos(Pos, s.Direction) : null;
            if (linked is not null && Api.World.BlockAccessor.GetBlockEntity(linked) is BlockEntityWaterTreatment e
                && e.Slots[half].Direction == s.Direction
                && e.Slots[half].Kind == (s.Kind == TreatmentKind.Drain ? TreatmentKind.Pipe : TreatmentKind.Drain))
                e.Remove(half, s.Kind == TreatmentKind.Pipe && drops);
            Changed();
            if (Slots.All(s => s.Kind == TreatmentKind.Empty)) Api.World.BlockAccessor.SetBlock(0, Pos);
        }
        finally { removing = false; }
    }
    internal void RemoveLinks()
    {
        if (removing) return;
        removing = true;
        try
        {
            // Whole-cell removals (explosions/world edits) must not leave an orphan companion.
            for (var half = 0; half < 2; half++)
            {
                var s = Slots[half];
                Slots[half] = new();
                var linked = s.Kind == TreatmentKind.Drain ? BlockWaterTreatment.PipePos(Pos, s.Direction)
                    : s.Kind == TreatmentKind.Pipe ? BlockWaterTreatment.BasinPos(Pos, s.Direction) : null;
                if (linked is not null && Api.World.BlockAccessor.GetBlockEntity(linked) is BlockEntityWaterTreatment other
                    && other.Slots[half].Direction == s.Direction
                    && other.Slots[half].Kind == (s.Kind == TreatmentKind.Drain ? TreatmentKind.Pipe : TreatmentKind.Drain))
                    other.Remove(half, s.Kind == TreatmentKind.Pipe);
            }
        }
        finally { removing = false; }
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        if (releaseFormatRejected) throw new InvalidDataException("Rejected release block state cannot be saved.");
        base.ToTreeAttributes(tree);
        tree.SetString("seraphFormat", "seraph.release.treatment");
        tree.SetInt("seraphVersion", 1);
        tree.SetString("treatmentSlots", JsonSerializer.Serialize(Slots));
    }
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        if (tree.HasAttribute("treatmentSlots") && (tree.GetString("seraphFormat") != "seraph.release.treatment" || tree.GetInt("seraphVersion") != 1))
        { releaseFormatRejected = true; throw new InvalidDataException("Unsupported release treatment format; original state preserved."); }
        base.FromTreeAttributes(tree, worldAccessForResolve);
        var json = tree.GetString("treatmentSlots", "");
        try
        {
            if (!tree.HasAttribute("treatmentSlots")) return; // A newly placed, unsaved entity.
            var saved = json.Length is > 0 and <= 16384 ? JsonSerializer.Deserialize<TreatmentSlot[]>(json,
                new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) : null;
            if (saved is { Length: 2 })
            {
                using var document = JsonDocument.Parse(json);
                for (var i = 0; i < saved.Length; i++)
                    if (saved[i] is not null && document.RootElement[i].TryGetProperty("Water", out var batch)
                        && batch.ValueKind == JsonValueKind.Object)
                        saved[i].Water = WaterQualityCompatibility.Read(batch);
            }
            if (saved is not { Length: 2 } || saved.Any(s => s is null || !s.Valid()))
                throw new InvalidDataException("Invalid release treatment slots.");
            Slots = saved;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
        { releaseFormatRejected = true; throw new InvalidDataException("Rejected treatment state; original data preserved.", e); }
        if (Api is ICoreClientAPI) MarkDirty(true);
        worldAccessForResolve.BlockAccessor.MarkBlockDirty(Pos);
    }

    private MeshData[] BuildMeshes(ITesselatorAPI tesselator)
    {
        if (Api is not ICoreClientAPI capi) return [];
        // A server update replaces Slots on the main thread. Do not publish a
        // cached mesh from an older worker snapshot after that update invalidates it.
        var slots = Slots;
        var result = new List<MeshData>();
        var source = tesselator.GetTextureSource(Block);
        for (var half = 0; half < 2; half++)
        {
            var s = slots[half];
            if (s.Kind == TreatmentKind.Empty) continue;
            var code = s.Kind == TreatmentKind.Pipe ? "drainpot-pipe" : s.Kind == TreatmentKind.Drain ? "drainpot" : "filterpot";
            var shape = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/" + code + ".json"));
            if (shape is null) continue;
            tesselator.TesselateShape(code, shape, out var mesh, source);
            Transform(mesh, s.Direction, half); result.Add(mesh);
            if (s.Kind == TreatmentKind.Pipe) continue;
            var layer = 0;
            foreach (var texture in new[] { s.Gravel is not null ? "gravel" : null, s.Charcoal is not null ? "charcoal" : null,
                s.Cloth is not null ? "linen" : null }.OfType<string>())
            {
                var height = 2 + layer++ * .65f;
                var surface = FixtureInteriorSurface.Build(TreatmentSurface(texture, height), [shape], height / 16, 0);
                foreach (var e in surface.Elements) { e.From![1] += height; e.To![1] += height; }
                tesselator.TesselateShape("filter-media", surface, out var media, source);
                Transform(media, s.Direction, half); result.Add(media);
            }
            if (s.Volume > .00001)
            {
                var water = capi.World.GetItem(new AssetLocation("game:waterportion"));
                if (water is null) continue;
                var content = new ItemStack(water);
                var props = BlockLiquidContainerBase.GetContainableProps(content);
                if (props?.Texture is null) continue;
                var height = WaterHeight(s);
                var template = TreatmentSurface("water", height);
                template.Elements[0].RenderPass = 4;
                var surface = FixtureInteriorSurface.Build(template, [shape], height / 16, 0);
                foreach (var e in surface.Elements) { e.From![1] += height; e.To![1] += height; }
                tesselator.TesselateShape("filter-water", surface, out var liquid, new ContainerTextureSource(capi, content, props.Texture));
                Transform(liquid, s.Direction, half); FixtureWaterMesh.Finish(liquid, capi, Pos, props); result.Add(liquid);
            }
        }
        return result.ToArray();
    }

    private static float WaterHeight(TreatmentSlot s)
    {
        // Start just above the basin floor or the installed filter layers.
        var layers = (s.Gravel is null ? 0 : 1) + (s.Charcoal is null ? 0 : 1) + (s.Cloth is null ? 0 : 1);
        var bottom = layers == 0 ? 1f : 2.1f + (layers - 1) * .65f;
        return bottom + (6.4f - bottom) * (float)Math.Clamp(s.Volume / WaterFiltration.ReservoirL, 0, 1);
    }

    private static Shape TreatmentSurface(string texture, float y) => new()
    {
        TextureWidth = 16, TextureHeight = 16,
        Elements = [new ShapeElement { From = [4, y - .1, 4], To = [12, y, 12],
            FacesResolved = [null!, null!, null!, null!,
                new() { Texture = texture, Uv = [4, 4, 12, 12], Enabled = true }, null!] }]
    };
    private static void Transform(MeshData mesh, int direction, int half)
        => mesh.Rotate(new Vec3f(.5f, 0, .5f), 0, -direction * GameMath.PIHALF, 0).Translate(0, half * .5f, 0);

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        foreach (var mesh in BuildMeshes(tesselator)) mesher.AddMeshData(mesh);
        return true;
    }
}


