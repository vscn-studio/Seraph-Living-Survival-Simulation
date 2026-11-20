using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>A compact, server-owned bath reservoir. Water is retained with the same quality metadata as carried water.</summary>
public sealed class BlockBathtub : Block
{
    private WorldInteraction[]? interactionHelp;

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        => interactionHelp ??= CreateInteractionHelp(world);

    private static WorldInteraction[] CreateInteractionHelp(IWorldAccessor world)
    {
        var containers = world.Blocks.OfType<BlockLiquidContainerBase>().Where(b => b.Code is not null).ToArray();
        return [
            new() { MouseButton = EnumMouseButton.Right, ActionLangCode = "vsslssmod:help-bath-fill",
                Itemstacks = containers.Select(b => new ItemStack(b)).ToArray() },
            new() { MouseButton = EnumMouseButton.Right, ActionLangCode = "vsslssmod:help-bath-take",
                Itemstacks = containers.Where(b => b.IsTopOpened).Select(b => new ItemStack(b)).ToArray() },
            new() { MouseButton = EnumMouseButton.Right, ActionLangCode = "vsslssmod:help-bath-soap",
                Itemstacks = world.Items.Where(i => i.Code is { } code &&
                    (code.Domain == "vsslssmod" && code.Path is "soap" or "soap-powder"
                     || code.Domain == "soapandlyecontinued" && code.Path is "soapbar-lye" or "soapbar-honey"))
                    .Select(i => new ItemStack(i)).ToArray() }
        ];
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        interactionHelp = null;
        base.OnUnloaded(api);
    }

    internal static bool IsCopper(Block? block) => block?.Code?.Domain == "vsslssmod" && block.Code.Path.StartsWith("copperbathtub", StringComparison.Ordinal);
    private static AssetLocation PartCode(Block? block, bool farEnd = false) => new("vsslssmod", IsCopper(block) ? farEnd ? "copperbathtub-end" : "copperbathtub-part" : "bathtub-part");
    private static readonly Cuboidf[] MasterSelection = [new(0, 0, 0, 2, 1, 1)];
    private static readonly Cuboidf[] PartSelection = [new(-1, 0, 0, 1, 1, 1)];
    private static readonly Cuboidf[] MasterBoxes =
    [
        new(0, 0.125f, 0.125f, 0.25f, 0.75f, 0.875f),
        new(0.1875f, 0.125f, 0, 1, 0.75f, 0.1875f),
        new(0.1875f, 0.125f, 0.8125f, 1, 0.75f, 1),
        new(0, 0.75f, 0.125f, 0.25f, 0.875f, 0.875f),
        new(0.1875f, 0.75f, 0, 1, 0.875f, 0.1875f),
        new(0.1875f, 0.75f, 0.8125f, 1, 0.875f, 1)
    ];
    private static readonly Cuboidf[] PartBoxes =
    [
        new(0.75f, 0.125f, 0.125f, 1, 0.75f, 0.875f),
        new(0, 0.125f, 0, 0.8125f, 0.75f, 0.1875f),
        new(0, 0.125f, 0.8125f, 0.8125f, 0.75f, 1),
        new(0.75f, 0.75f, 0.125f, 1, 0.875f, 0.875f),
        new(0, 0.75f, 0, 0.8125f, 0.875f, 0.1875f),
        new(0, 0.75f, 0.8125f, 0.8125f, 0.875f, 1)
    ];

    private static readonly Cuboidf[][] RotatedMasterBoxes = FixtureOrientation.Boxes(MasterBoxes);
    private static readonly Cuboidf[][] RotatedPartBoxes = FixtureOrientation.Boxes(PartBoxes);
    private static readonly Cuboidf[][] RotatedMasterSelection = FixtureOrientation.Boxes(MasterSelection);
    private static readonly Cuboidf[][] RotatedPartSelection = FixtureOrientation.Boxes(PartSelection);
    private static int Direction(IBlockAccessor accessor, BlockPos pos) => FixtureOrientation.PlacementDirection
        ?? (accessor.GetBlockEntity(pos) as BlockEntityBathtub)?.Direction ?? 0;
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (!IsCopper(this)) return (IsPart(this) ? RotatedPartBoxes : RotatedMasterBoxes)[Direction(blockAccessor, pos)];
        return FixtureOrientation.Boxes(CollisionBoxes ?? [])[Direction(blockAccessor, pos)];
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        => IsCopper(this) ? FixtureOrientation.Boxes(SelectionBoxes ?? [])[Direction(blockAccessor, pos)]
            : (IsPart(this) ? RotatedPartSelection : RotatedMasterSelection)[Direction(blockAccessor, pos)];

    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => false;

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
        => Lang.Get(IsCopper(this) ? "vsslssmod:block-copperbathtub" : "vsslssmod:block-bathtub");

    internal static bool IsPart(Block? block) => block?.Code?.Domain == "vsslssmod" && block.Code.Path is "bathtub-part" or "copperbathtub-part" or "copperbathtub-end";

    private static int CopperPartOffset(Block? block) => block?.Code?.Path == "copperbathtub-end" ? 1 : -1;

    private static BlockPos GetMasterPos(IBlockAccessor accessor, BlockPos pos, Block block)
        => IsPart(block) ? IsCopper(block)
            ? FixtureOrientation.BathOffset(pos, Direction(accessor, pos), -CopperPartOffset(block))
            : FixtureOrientation.BathPartner(pos, Direction(accessor, pos), true) : pos;

    internal static BlockEntityBathtub? GetMaster(IWorldAccessor world, BlockPos pos)
        => GetMaster(world.BlockAccessor,pos);

    internal static BlockEntityBathtub? GetMaster(IBlockAccessor accessor, BlockPos pos)
    {
        var block = accessor.GetBlock(pos);
        if (block is not BlockBathtub) return null;
        var masterPos = GetMasterPos(accessor, pos, block);
        return accessor.GetBlockEntity(masterPos) is BlockEntityBathtub bath && !IsPart(bath.Block) ? bath : null;
    }

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack,
        BlockSelection blockSel, ref string failureCode)
    {
        BlockPos? masterPos = null;
        BlockPos? partPos = null;
        PlacementSnapshot[]? original = null;
        try
        {
            // Copper bath is centered on the clicked cell; wooden bath keeps its legacy edge anchor.
            var direction = (FixtureOrientation.Facing(byPlayer) + 3) & 3;
            using var placement = FixtureOrientation.Placement(direction);
            partPos = blockSel.Position.Copy();
            var copper = IsCopper(this);
            masterPos = copper ? partPos : FixtureOrientation.BathPartner(partPos, direction, true);
            var masterSelection = new BlockSelection { Position = masterPos, Face = BlockFacing.UP };
            var partPosA = copper ? FixtureOrientation.BathOffset(masterPos, direction, -1) : partPos;
            var partPosB = copper ? FixtureOrientation.BathOffset(masterPos, direction, 1) : null;
            var partSelection = new BlockSelection { Position = partPosA, Face = BlockFacing.UP };
            if (!CanPlaceBlock(world, byPlayer, masterSelection, ref failureCode)) return false;

            var part = world.BlockAccessor.GetBlock(PartCode(this));
            if (part is null || !part.CanPlaceBlock(world, byPlayer, partSelection, ref failureCode)) return false;
            Block? farPart = null;
            BlockSelection? farSelection = null;
            if (copper)
            {
                farPart = world.BlockAccessor.GetBlock(PartCode(this, true));
                farSelection = new BlockSelection { Position = partPosB, Face = BlockFacing.UP };
                if (farPart is null || !farPart.CanPlaceBlock(world, byPlayer, farSelection, ref failureCode)) return false;
            }
            var positions = copper ? new[] { masterPos, partPosA, partPosB! } : new[] { masterPos, partPosA };
            original = positions.Select(p => PlacementSnapshot.Capture(world, p)).ToArray();
            // The whole assembly is one placement transaction, including replaceable contents.
            if (!part.DoPlaceBlock(world, byPlayer, partSelection, itemstack)
                || copper && !farPart!.DoPlaceBlock(world, byPlayer, farSelection!, itemstack)
                || !DoPlaceBlock(world, byPlayer, masterSelection, itemstack))
            { RollbackPlacement(world, original); return false; }
            if (world.BlockAccessor.GetBlockEntity(masterPos) is not BlockEntityBathtub masterEntity)
            { RollbackPlacement(world, original); return false; }
            masterEntity.SetDirection(direction);
            foreach (var p in positions.Skip(1))
            {
                if (world.BlockAccessor.GetBlockEntity(p) is not BlockEntityBathtub partEntity)
                { RollbackPlacement(world, original); return false; }
                partEntity.SetDirection(direction);
            }
            return true;
        }
        catch (Exception ex)
        {
            apiLogger(world)?.Warning("[vsslssmod] Bathtub placement failed and was rolled back: {0}: {1}",
                ex.GetType().Name, ex.Message);
            try
            {
                if (original is not null) RollbackPlacement(world, original);
            }
            catch (Exception rollbackError)
            {
                apiLogger(world)?.Warning("[vsslssmod] Bathtub rollback failed: {0}: {1}",
                    rollbackError.GetType().Name, rollbackError.Message);
            }
            failureCode = "cannotplace";
            return false;
        }
    }

    private sealed record PlacementSnapshot(BlockPos Pos, int Id, TreeAttribute? Tree)
    {
        internal static PlacementSnapshot Capture(IWorldAccessor world, BlockPos pos)
        {
            TreeAttribute? tree = null;
            if (world.BlockAccessor.GetBlockEntity(pos) is { } entity)
            { tree = new(); entity.ToTreeAttributes(tree); }
            return new(pos.Copy(), world.BlockAccessor.GetBlock(pos).BlockId, tree);
        }
    }
    private static void RollbackPlacement(IWorldAccessor world, PlacementSnapshot[] original)
    {
        foreach (var state in original)
            if (world.BlockAccessor.GetBlock(state.Pos) is BlockBathtub) world.BlockAccessor.SetBlock(0, state.Pos);
        foreach (var state in original)
        {
            world.BlockAccessor.SetBlock(state.Id, state.Pos);
            if (state.Tree is not null && world.BlockAccessor.GetBlockEntity(state.Pos) is { } entity)
                entity.FromTreeAttributes(state.Tree, world);
        }
    }

    private static ILogger? apiLogger(IWorldAccessor world) => (world.Api as ICoreAPI)?.Logger;

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (blockSel is not null && GetMaster(world, blockSel.Position) is { } bath)
            return bath.OnInteract(byPlayer);
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var baseText = base.GetPlacedBlockInfo(world, pos, forPlayer);
        return GetMaster(world, pos) is BlockEntityBathtub bath
            ? baseText + (string.IsNullOrWhiteSpace(baseText) ? "" : "\n") + bath.InfoText()
            : baseText;
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        if (IsCopper(this))
        {
            var center = IsPart(this) ? GetMasterPos(world.BlockAccessor, pos, this) : pos;
            foreach (var p in new[] { center, FixtureOrientation.BathOffset(center, Direction(world.BlockAccessor, center), -1), FixtureOrientation.BathOffset(center, Direction(world.BlockAccessor, center), 1) })
                if (world.BlockAccessor.GetBlock(p) is BlockBathtub bath && IsCopper(bath)) world.BlockAccessor.SetBlock(0, p);
        }
        else if (IsPart(this))
        {
            var masterPos = FixtureOrientation.BathPartner(pos, Direction(world.BlockAccessor, pos), true);
            if (world.BlockAccessor.GetBlock(masterPos).Code?.Domain == "vsslssmod" && world.BlockAccessor.GetBlock(masterPos).Code.Path == "bathtub")
                world.BlockAccessor.SetBlock(0, masterPos);
        }
        else if (Code?.Domain == "vsslssmod" && Code.Path == "bathtub")
        {
            var partPos = FixtureOrientation.BathPartner(pos, Direction(world.BlockAccessor, pos), false);
            if (IsPart(world.BlockAccessor.GetBlock(partPos))) world.BlockAccessor.SetBlock(0, partPos);
        }
        base.OnBlockRemoved(world, pos);
    }

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        => [OnPickBlock(world, pos)];

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        var master = GetMaster(world, pos);
        return new(world.BlockAccessor.GetBlock(new AssetLocation("vsslssmod", IsCopper(master?.Block) ? "copperbathtub" : "bathtub")));
    }
}

public class BlockEntityBathtub : BlockEntity
{
    private bool releaseFormatRejected;

    internal const double CapacityL = 200;
    internal const double CopperCapacityL = 300;
    internal virtual double ReservoirCapacity => IsCopper ? CopperCapacityL : CapacityL;
    private WaterBatch? water;
    private Dictionary<string, int> soapCharges = new(StringComparer.Ordinal);
    private const int MaxSoapCharges = 5;
    private double visualVolume;
    private MeshData? waterMesh;
    private MeshData? structureMesh;
    private long treatmentListener;
    private const double TreatmentTickSeconds = 30;
    internal int Direction { get; private set; }
    private BlockPos PartnerPos => IsCopper
        ? IsPart ? FixtureOrientation.BathOffset(Pos, Direction, Block?.Code?.Path == "copperbathtub-end" ? -1 : 1)
            : FixtureOrientation.BathOffset(Pos, Direction, 1)
        : FixtureOrientation.BathPartner(Pos, Direction, IsPart);
    internal void SetDirection(int direction)
    {
        Direction = direction & 3; structureMesh = null; waterMesh = null;
        MarkDirty(true); Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    private bool IsPart => BlockBathtub.IsPart(Block);
    private bool IsCopper => BlockBathtub.IsCopper(Block);

    protected virtual void RedrawWater()
    {
        visualVolume = WaterLitres;
        if (!IsPart && Api?.World.BlockAccessor.GetBlockEntity(PartnerPos) is BlockEntityBathtub part)
        {
            part.visualVolume = visualVolume;
            part.MarkDirty(true);
        }
        if (!IsPart && IsCopper && Api?.World.BlockAccessor.GetBlockEntity(FixtureOrientation.BathOffset(Pos, Direction, -1)) is BlockEntityBathtub other)
        { other.visualVolume = visualVolume; other.MarkDirty(true); }
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
        Api?.World.BlockAccessor.MarkBlockDirty(PartnerPos);
    }

    public double WaterLitres => Math.Max(0, water?.VolumeL ?? 0);
    internal int SoapChargeCount => soapCharges.Values.Sum();
    internal WaterBatch? Water => water;

    internal virtual bool Contains(Vec3d feet, int dimension)
    {
        var box = FixtureOrientation.Rotate(IsCopper
            ? new Cuboidf(-.8125f, 0, .09375f, 1.8125f, .625f, .90625f)
            : new Cuboidf(.25f, 0, .1875f, 1.75f, .875f, .8125f), Direction);
        return dimension == Pos.dimension && feet.X >= Pos.X + box.X1 && feet.X <= Pos.X + box.X2
            && feet.Z >= Pos.Z + box.Z1 && feet.Z <= Pos.Z + box.Z2
            && feet.Y >= Pos.Y && feet.Y <= Pos.Y + box.Y2;
    }

    internal void RestoreWater(WaterBatch batch) { water = batch; RedrawWater(); }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        try
        {
            if (api is ICoreClientAPI capi) RebuildWaterMesh(capi);
            else if (!IsPart)
            {
                RedrawWater();
                treatmentListener = RegisterGameTickListener(_ => TickWaterTreatment(), (int)(TreatmentTickSeconds * 1000));
            }
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[vsslssmod] Bathtub block entity initialization failed: {0}: {1}",
                ex.GetType().Name, ex.Message);
            waterMesh = null;
        }
    }

    private void TickWaterTreatment()
    {
        if (Api?.Side != EnumAppSide.Server || IsPart || water is null) return;
        var config = Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig;
        var climate = WaterQuality.Ambient(Api.World, Pos);
        var ambient = Api is ICoreServerAPI sapi
            ? MicroclimateAdapter.RoomAirTemperature(sapi, Pos, climate, config) : climate;
        var next = BathWaterTreatment.Advance(water, ambient, TreatmentTickSeconds,
            config.BathWaterCoolingHalfLifeSeconds, config.BathWaterThermalKillPerDegreeSecond);
        if (IsCopper && next.VolumeL > 0)
        {
            // Direct conduction is an energy input, not a change to the 90-minute
            // air-cooling boundary. A burning native firepit supplies 20 kJ/s.
            var energy = CopperHeatStrength() * 2 * TreatmentTickSeconds;
            if (energy > 0) next = WaterHeating.AtTemperature(next,
                Math.Min(100, next.TemperatureC + energy / (next.VolumeL * 4.184)));
        }
        if (Math.Abs(next.TemperatureC - water.TemperatureC) < .01
            && Math.Abs(next.EntericHazardPerL - water.EntericHazardPerL) < 1e-9
            && next.PathogensPerL.All(pair => Math.Abs(pair.Value - water.PathogensPerL.GetValueOrDefault(pair.Key)) < 1e-9)) return;
        water = next;
        RedrawWater();
    }

    private double CopperHeatStrength()
    {
        var blocks = Api.World.BlockAccessor;
        double strength = 0;
        for (int offset = -1; offset <= 1; offset++)
        {
            var receiver = FixtureOrientation.BathOffset(Pos, Direction, offset);
            var below = receiver.DownCopy();
            if (blocks.GetChunkAtBlockPos(below) is null) continue;
            var source = blocks.GetBlockEntity(below) as IHeatSource ?? blocks.GetBlock(below) as IHeatSource;
            if (source is null) continue;
            try
            {
                var heat = source.GetHeatStrength(Api.World, below, receiver);
                if (float.IsFinite(heat)) strength += Math.Clamp(heat, 0, 10);
            }
            catch { /* Ignore an unavailable third-party source without stopping the bath tick. */ }
        }
        return strength;
    }

    protected virtual bool CanUse(IPlayer player)
    {
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)
            || !Api.World.Claims.TryAccess(player, PartnerPos, EnumBlockAccessFlags.Use)) return false;
        return !IsCopper || Api.World.Claims.TryAccess(player,
            FixtureOrientation.BathOffset(Pos, Direction, -1), EnumBlockAccessFlags.Use);
    }

    public virtual bool OnInteract(IPlayer player)
    {
        if (IsPart && Api?.World.BlockAccessor.GetBlockEntity(PartnerPos) is BlockEntityBathtub master)
            return master.OnInteract(player);
        if (Api?.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return true;
        if (!CanUse(player)) return true;
        var owner = Api.ModLoader.GetModSystem<SeraphModSystem>().Server;
        if (owner is null) return true;
        if (TryAddSoap(serverPlayer)) return true;
        if (TryExtract(serverPlayer) || TryFill(serverPlayer, owner.InteractionConfig)) return true;
        return true;
    }

    private bool TryAddSoap(IServerPlayer player)
    {
        var slot = player.InventoryManager.ActiveHotbarSlot;
        if (slot is null || slot.Itemstack is not { } stack || !IsBathSoap(stack)) return false;
        var total = soapCharges.Values.Sum();
        if (total >= MaxSoapCharges)
        {
            return true;
        }
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative) stack.StackSize--;
        if (stack.StackSize <= 0) slot.Itemstack = null;
        soapCharges[""] = soapCharges.GetValueOrDefault("") + 1;
        slot.MarkDirty();
        MarkDirty(true);
        return true;
    }

    private static bool IsBathSoap(ItemStack? stack)
    {
        if (stack?.Collectible.Code is not { } code) return false;
        return code.Domain == "vsslssmod" && code.Path is "soap" or "soap-powder"
            || code.Domain == "soapandlyecontinued" && code.Path is "soapbar-lye" or "soapbar-honey";
    }

    internal bool TrySpendSoapCharge()
    {
        var count = soapCharges.GetValueOrDefault("");
        if (count <= 0) return false;
        if (count == 1) soapCharges.Remove("");
        else soapCharges[""] = count - 1;
        MarkDirty(true);
        return true;
    }

    internal void RestoreSoapCharge()
    {
        soapCharges[""] = soapCharges.GetValueOrDefault("") + 1;
        MarkDirty(true);
    }

    // Both gravity inflow and held-container filling use this reservoir boundary.
    internal virtual double AcceptWater(WaterBatch incoming)
    {
        if (IsPart) return BlockBathtub.GetMaster(Api.World, Pos)?.AcceptWater(incoming) ?? 0;
        if (incoming.Validate() is not null) return 0;
        var accepted = Math.Min(incoming.VolumeL, Math.Max(0, ReservoirCapacity - WaterLitres));
        if (accepted <= 0) return 0;
        var actual = incoming with { VolumeL = accepted };
        water = water is null ? actual : water.Mix(actual);
        RedrawWater();
        return accepted;
    }

    internal bool TryFill(IServerPlayer player, Seraph.Application.SeraphServerConfig config)
    {
        if (WaterLitres >= ReservoirCapacity - 0.001) return false;
        var slot = player.InventoryManager.ActiveHotbarSlot;
        if (slot.Itemstack?.Collectible is not BlockLiquidContainerBase container) return false;
        var content = container.GetContent(slot.Itemstack);
        var climate = WaterQuality.Ambient(Api!.World, Pos);
        var ambient = Api is ICoreServerAPI sapi
            ? MicroclimateAdapter.RoomAirTemperature(sapi, Pos, climate, config) : climate;
        var batch = WaterQuality.ReadLiquidAt(Api!.World, content, ambient, config);
        var props = content is null ? null : BlockLiquidContainerBase.GetContainableProps(content);
        if (batch is not { VolumeL: > 0 } || props is not { Containable: true, ItemsPerLitre: > 0 }) return false;
        var amount = Math.Min(batch.VolumeL, ReservoirCapacity - WaterLitres);
        var count = (int)Math.Floor(amount * props.ItemsPerLitre + 1e-6);
        if (count <= 0) return false;
        // Use the native single-container split so stacked buckets cannot duplicate water.
        var moved = container.SplitStackAndPerformAction(player.Entity, slot,
            stack => container.TryTakeContent(stack, count)?.StackSize ?? 0);
        if (moved <= 0) return false;
        var actual = batch with { VolumeL = moved / (double)props.ItemsPerLitre };
        AcceptWater(actual);
        slot.MarkDirty();
        return true;
    }

    internal bool TryExtract(IServerPlayer player)
    {
        var slot = player.InventoryManager.ActiveHotbarSlot;
        if (water is null || slot.Itemstack?.Collectible is not BlockLiquidContainerBase container
            || !container.IsTopOpened || container.GetContent(slot.Itemstack) is not null) return false;
        var item = Api.World.GetItem(new AssetLocation("game", water.SaltGramsPerL > 5 ? "saltwaterportion" : "waterportion"));
        if (item is null) return false;
        var liquid = new ItemStack(item);
        var props = BlockLiquidContainerBase.GetContainableProps(liquid);
        if (props is not { ItemsPerLitre: > 0 }) return false;
        liquid.StackSize = (int)Math.Floor(water.VolumeL * props.ItemsPerLitre + 1e-6);
        if (liquid.StackSize <= 0) return false;
        WaterMetadata.WriteAt(Api!.World, liquid, water);
        var put = container.SplitStackAndPerformAction(player.Entity, slot,
            stack => container.TryPutLiquid(stack, liquid, (float)water.VolumeL));
        if (put <= 0) return false;
        TryConsume(put / (double)props.ItemsPerLitre, out _);
        slot.MarkDirty();
        return true;
    }

    internal void AcceptBathWaste(double dirt, IReadOnlyDictionary<string, double> pathogens)
    {
        if (water is null) return;
        water = water with { Turbidity = Math.Clamp(water.Turbidity + dirt / water.VolumeL, 0, 1),
            PathogensPerL = PathogenLoads.Add(water.PathogensPerL, PathogenLoads.Scale(pathogens, 1 / water.VolumeL)) };
        RedrawWater();
    }

    internal bool TryConsume(double amount, out WaterBatch consumed)
    {
        consumed = null!;
        if (amount <= 0 || water is null || water.VolumeL < amount) return false;
        consumed = water with { VolumeL = amount };
        var remain = water.VolumeL - amount;
        water = remain <= 0.001 ? null : water with { VolumeL = remain };
        RedrawWater();
        return true;
    }

    internal void Restore(WaterBatch batch)
    {
        water = water is null ? batch : water.Mix(batch);
        RedrawWater();
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        if (Api is not ICoreClientAPI capi) return false;
        // Initialization can precede atlas registration; retry at terrain tessellation.
        if (waterMesh is null && visualVolume > 0) RebuildWaterMesh(capi, tesselator);
        if (structureMesh is null && capi.TesselatorManager.GetDefaultBlockMesh(Block) is { } baseMesh)
            structureMesh = FixtureOrientation.Mesh(baseMesh, Direction);
        if (structureMesh is null) return false;
        mesher.AddMeshData(structureMesh);
        if (waterMesh is { } mesh) mesher.AddMeshData(mesh);
        return true;
    }

    protected virtual void RebuildWaterMesh(ICoreClientAPI capi, ITesselatorAPI? tesselator = null)
    {
        waterMesh = null;
        if (visualVolume <= 0) return;
        try
        {
            var item = capi.World.GetItem(new AssetLocation("game", "waterportion"));
            if (item is null) return;
            var content = new ItemStack(item);
            var props = BlockLiquidContainerBase.GetContainableProps(content);
            if (props?.Texture is null) return;
            var shape = Shape.TryGet(capi, new AssetLocation("vsslssmod", "shapes/block/bathtub-water.json"));
            if (shape is null) return;
            var fill = (float)Math.Clamp(visualVolume / ReservoirCapacity, 0, 1);
            var height = IsCopper ? (.75f + 8.25f * fill) / 16 : .125f + .625f * fill;
            var cells = IsCopper
                ? new[] { "copperbathtub-part", "copperbathtub", "copperbathtub-end" }
                : new[] { "bathtub", "bathtub-part" };
            var cellIndex = IsCopper ? Block.Code?.Path switch { "copperbathtub-part" => 0, "copperbathtub-end" => 2, _ => 1 }
                : IsPart ? 1 : 0;
            var cellShapes = cells.Select(code => Shape.TryGet(capi, new AssetLocation($"vsslssmod:shapes/block/{code}.json"))).ToArray();
            if (cellShapes.Any(s => s is null)) return;
            shape = FixtureInteriorSurface.Build(shape, cellShapes!, height, cellIndex);
            (tesselator ?? capi.Tesselator).TesselateShape("vsslssmod-bathtub-water", shape, out var mesh,
                new ContainerTextureSource(capi, content, props.Texture), null, props.GlowLevel);
            if (mesh is not { VerticesCount: > 0 }) return;
            // Each cell owns one half of the surface; both use the master's synced water level.
            mesh.Translate(0, height, 0);
            // Match the well wall: native water texture, liquid pass and the
            // container shader attributes that suppress ocean waves and foam.
            FixtureWaterMesh.Finish(mesh, capi, Pos, props);
            waterMesh = FixtureOrientation.Mesh(mesh, Direction);
        }
        catch (Exception ex)
        {
            // A visual mesh is optional. Keep the block usable if a shape or
            // microblock renderer rejects the current game/mod combination.
            capi.Logger.Warning("[vsslssmod] Bathtub water mesh was skipped: {0}: {1}",
                ex.GetType().Name, ex.Message);
        }
    }

    public virtual string InfoText()
    {
        var level = Lang.Get("vsslssmod:bathtub-level", WaterLitres.ToString("0.0"), ReservoirCapacity.ToString("0"))
            + "\n" + LocalizedText.Get("vsslssmod:bathtub-cost")
            + "\n" + LocalizedText.Get("vsslssmod:bathtub-soap-status", soapCharges.Values.Sum(), MaxSoapCharges);
        return water is null ? level : level + "\n" + string.Join("\n", WaterInfoText.Lines(water,
            (key, args) => LocalizedText.Get("vsslssmod:" + key, args), includeVolume: false, includeWaste: false));
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        if (releaseFormatRejected) throw new InvalidDataException("Rejected release block state cannot be saved.");
        base.ToTreeAttributes(tree);
        tree.SetString("seraphFormat", "seraph.release.bath");
        tree.SetInt("seraphVersion", 1);
        tree.SetInt("direction", Direction);
        tree.SetDouble("visualWaterVolume", IsPart ? visualVolume : WaterLitres);
        tree.SetString("soapCharges", JsonSerializer.Serialize(soapCharges));
        if (water is null) return;
        tree.SetDouble("waterVolume", water.VolumeL);
        tree.SetString("waterPathogens", JsonSerializer.Serialize(water.PathogensPerL));
        tree.SetDouble("waterTemperature", water.TemperatureC);
        tree.SetDouble("waterSalt", water.SaltGramsPerL);
        tree.SetDouble("waterHazard", water.EntericHazardPerL);
        tree.SetDouble("waterChemicalHazard", water.ChemicalHazardPerL);
        tree.SetDouble("waterTurbidity", water.Turbidity);
        tree.SetDouble("waterBaselineTurbidity", water.NaturalTurbidity);
        tree.SetBool("waterUnverified", water.Unverified);
        tree.SetString("waterSource", water.Source);
        tree.SetString("waterFormat", water.Format);
        tree.SetInt("waterQualityVersion", water.QualityVersion);
        tree.SetString("waterOriginSource", water.OriginSource ?? "");
        tree.SetInt("waterTreatmentStage", (int)water.TreatmentStage);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        if ((tree.HasAttribute("waterVolume") || tree.HasAttribute("soapCharges") || tree.HasAttribute("visualWaterVolume") || tree.HasAttribute("direction")) && (tree.GetString("seraphFormat") != "seraph.release.bath" || tree.GetInt("seraphVersion") != 1))
        { releaseFormatRejected = true; throw new InvalidDataException("Unsupported release bath format; original state preserved."); }
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Direction = tree.GetInt("direction", 0);
        if (Direction is < 0 or > 3)
        { releaseFormatRejected = true; throw new InvalidDataException("Invalid release bath direction."); }
        structureMesh = null;
        try
        {
            var soapJson = tree.GetString("soapCharges", "{}");
            var parsedCharges = soapJson.Length <= 1024 ? JsonSerializer.Deserialize<Dictionary<string, int>>(soapJson) : null;
            if (parsedCharges is null || parsedCharges.Count > 1
                || parsedCharges.Any(pair => pair.Key != "" || pair.Value is <= 0 or > MaxSoapCharges))
                throw new InvalidDataException("Invalid release soap charges.");
            soapCharges = new(parsedCharges, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { releaseFormatRejected = true; throw new InvalidDataException("Rejected bath state; original data preserved.", e); }
        try
        {
        var volume = tree.GetDouble("waterVolume", 0);
        if (!double.IsFinite(volume) || volume < 0 || volume > ReservoirCapacity) throw new InvalidDataException("Invalid bath water volume.");
        IReadOnlyDictionary<string, double> pathogens = PathogenLoads.Empty;
        try
        {
            var json = tree.GetString("waterPathogens", "{}");
            var parsed = json.Length <= 4096 ? JsonSerializer.Deserialize<Dictionary<string, double>>(json) : null;
            if (!PathogenLoads.Valid(parsed)) throw new InvalidDataException("Invalid bath pathogens.");
            pathogens = parsed!;
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid bath pathogen data.", e); }

        var origin = tree.GetString("waterOriginSource", "");
        water = volume <= 0 ? null : WaterQualityCompatibility.RequireCurrent(new WaterBatch
        {
            Format = tree.GetString("waterFormat", ""),
            QualityVersion = tree.GetInt("waterQualityVersion", 0),
            OriginSource = origin.Length == 0 ? null : origin,
            TreatmentStage = (WaterTreatmentStage)tree.GetInt("waterTreatmentStage", 0),
            PathogensPerL = pathogens, VolumeL = volume, TemperatureC = tree.GetDouble("waterTemperature", 15),
            SaltGramsPerL = tree.GetDouble("waterSalt", 0),
            EntericHazardPerL = tree.GetDouble("waterHazard", 0),
            ChemicalHazardPerL = tree.GetDouble("waterChemicalHazard", 0),
            Turbidity = tree.GetDouble("waterTurbidity", 0.15),
            BaselineTurbidity = tree.HasAttribute("waterBaselineTurbidity") ? tree.GetDouble("waterBaselineTurbidity") : null,
            Unverified = tree.GetBool("waterUnverified", true), Source = tree.GetString("waterSource", "unverified")
        });
        if (water?.Validate() is not null) throw new InvalidDataException("Invalid bath water state.");
        visualVolume = tree.GetDouble("visualWaterVolume", volume);
        if (!double.IsFinite(visualVolume) || visualVolume < 0 || visualVolume > ReservoirCapacity)
            throw new InvalidDataException("Invalid bath visual volume.");
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { releaseFormatRejected = true; throw new InvalidDataException("Rejected bath water state; original data preserved.", e); }
        if (Api?.Side == EnumAppSide.Client)
        {
            RebuildWaterMesh((ICoreClientAPI)Api);
            worldAccessForResolve.BlockAccessor.MarkBlockDirty(Pos);
            worldAccessForResolve.BlockAccessor.MarkBlockDirty(PartnerPos);
        }
    }

    public override void OnBlockRemoved()
    {
        if (treatmentListener != 0) UnregisterGameTickListener(treatmentListener);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        if (treatmentListener != 0) UnregisterGameTickListener(treatmentListener);
        base.OnBlockUnloaded();
    }
}
