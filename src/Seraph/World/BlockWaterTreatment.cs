using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Seraph.World;

/// <summary>Inventory fixtures place into a shared cell with two independently selectable halves.</summary>
public sealed class BlockWaterTreatment : Block
{
    internal static readonly (int X, int Z)[] Directions = [(0, -1), (1, 0), (0, 1), (-1, 0)];
    internal static BlockPos PipePos(BlockPos basin, int direction) => basin.AddCopy(Directions[direction].X, 0, Directions[direction].Z);
    internal static BlockPos BasinPos(BlockPos pipe, int direction) => pipe.AddCopy(-Directions[direction].X, 0, -Directions[direction].Z);
    internal TreatmentKind ItemKind => Code.Path == "drainpot" ? TreatmentKind.Drain : TreatmentKind.Filter;
    internal static int Half(BlockSelection selection, BlockEntityWaterTreatment entity)
    {
        var occupied = Enumerable.Range(0, 2).Where(h => entity.Slots[h].Kind != TreatmentKind.Empty).ToArray();
        return occupied.Length == 0 ? 0 : occupied[Math.Clamp(selection.SelectionBoxIndex, 0, occupied.Length - 1)];
    }

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        var half = blockSel.Face == BlockFacing.DOWN ? 1 : blockSel.Face == BlockFacing.UP ? 0 : blockSel.HitPosition.Y >= .5 ? 1 : 0;
        // The engine offsets ordinary placement to the next whole block. Reuse the
        // selected cell when clicking the top of its lower half (or bottom of its upper half).
        var target = blockSel.Position.Copy();
        if (blockSel.DidOffset)
        {
            var original = target.AddCopy(blockSel.Face.Opposite);
            if (world.BlockAccessor.GetBlockEntity(original) is BlockEntityWaterTreatment existing)
            {
                var selected = Half(blockSel, existing);
                if (blockSel.Face == BlockFacing.UP && selected == 0 && existing.Slots[1].Kind == TreatmentKind.Empty)
                { target = original; half = 1; }
                else if (blockSel.Face == BlockFacing.DOWN && selected == 1 && existing.Slots[0].Kind == TreatmentKind.Empty)
                { target = original; half = 0; }
            }
        }
        var direction = FixtureOrientation.Facing(byPlayer);
        return Place(world, byPlayer, itemstack.Block.Code.Path == "drainpot" ? TreatmentKind.Drain : TreatmentKind.Filter,
            target, half, direction, ref failureCode);
    }

    internal static bool Place(IWorldAccessor world, IPlayer player, TreatmentKind kind, BlockPos pos, int half, int direction, ref string failure)
    {
        if (kind is not (TreatmentKind.Filter or TreatmentKind.Drain) || half is < 0 or > 1 || direction is < 0 or > 3) return false;
        var block = world.GetBlock(new AssetLocation("vsslssmod:watertreatment"));
        if (block is null) return false;
        var positions = kind == TreatmentKind.Drain ? new[] { pos, PipePos(pos, direction) } : new[] { pos };
        foreach (var target in positions)
        {
            if (world.BlockAccessor.GetChunkAtBlockPos(target) is null) { failure = "vsslssmod:filter-unloaded"; return false; }
            if (!world.Claims.TryAccess(player, target, EnumBlockAccessFlags.BuildOrBreak)) { failure = "claimed"; return false; }
            if (world.BlockAccessor.GetBlockEntity(target) is BlockEntityWaterTreatment existing)
            {
                if (existing.Slots[half].Kind != TreatmentKind.Empty) { failure = "notreplaceable"; return false; }
            }
            else if (!world.BlockAccessor.GetBlock(target).IsReplacableBy(block)) { failure = "notreplaceable"; return false; }
            var boxes = new[] { Bounds(target.Equals(pos) ? kind : TreatmentKind.Pipe, direction, half) };
            if (world.GetIntersectingEntities(target, boxes, e => e.IsInteractable).Length > 0)
            { failure = "entityintersecting"; return false; }
        }
        // The client only predicts success; authoritative state is synchronized by the server.
        if (world.Side != EnumAppSide.Server) return true;
        var created = new List<BlockPos>();
        foreach (var target in positions)
        {
            if (world.BlockAccessor.GetBlockEntity(target) is not BlockEntityWaterTreatment)
            {
                world.BlockAccessor.SetBlock(block.BlockId, target);
                created.Add(target);
            }
            if (world.BlockAccessor.GetBlockEntity(target) is BlockEntityWaterTreatment) continue;
            foreach (var rollback in created) world.BlockAccessor.SetBlock(0, rollback);
            return false;
        }
        foreach (var target in positions)
        {
            var entity = (BlockEntityWaterTreatment)world.BlockAccessor.GetBlockEntity(target);
            entity.Slots[half] = new TreatmentSlot { Kind = target.Equals(pos) ? kind : TreatmentKind.Pipe,
                Direction = direction, OwnerUid = player.PlayerUID };
            entity.Changed();
        }
        return true;
    }

    internal static Cuboidf Bounds(TreatmentKind kind, int direction, int half)
    {
        var b = kind == TreatmentKind.Pipe ? new Cuboidf(7f/16, 1f/18, 7f/16, 9f/16, 2f/9, 1)
            : new Cuboidf(2f/16, 0, kind == TreatmentKind.Drain ? 0 : 2f/16, 14f/16, .5f, 14f/16);
        for (var i = 0; i < direction; i++) b = new Cuboidf(1 - b.Z2, b.Y1, b.X1, 1 - b.Z1, b.Y2, b.X2);
        b.Y1 += half * .5f; b.Y2 += half * .5f;
        return b;
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor accessor, BlockPos pos)
        => accessor.GetBlockEntity(pos) is BlockEntityWaterTreatment e
            ? e.Slots.Select((s, h) => s.Kind == TreatmentKind.Empty ? null : Bounds(s.Kind, s.Direction, h)).OfType<Cuboidf>().ToArray()
            : [Bounds(ItemKind, 0, 0)];
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor accessor, BlockPos pos) => GetSelectionBoxes(accessor, pos);
    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => true;

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection selection)
    {
        if (world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityWaterTreatment entity) return false;
        var half = Half(selection, entity);
        if (byPlayer.InventoryManager.ActiveHotbarSlot.Itemstack?.Block is BlockWaterTreatment held && held.Code.Path != "watertreatment")
            return false; // Native placement consumes exactly one inventory fixture and handles half merging above.
        return entity.Interact(byPlayer, half);
    }

    private static (BlockEntityWaterTreatment Entity, int Half)? Selected(IWorldAccessor world, BlockPos pos, IPlayer? player)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityWaterTreatment entity) return null;
        var selected = player?.CurrentBlockSelection;
        var half = selected is not null && selected.Position.Equals(pos) ? Half(selected, entity)
            : entity.Slots[0].Kind != TreatmentKind.Empty ? 0 : 1;
        return entity.Master(half);
    }

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        var chosen = Selected(world, pos, world is Vintagestory.API.Client.IClientWorldAccessor c ? c.Player : null);
        var kind = chosen?.Entity.Slots[chosen.Value.Half].Kind ?? ItemKind;
        return LocalizedText.Get("vsslssmod:block-" + (kind == TreatmentKind.Drain ? "drainpot" : "filterpot"));
    }
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        => Selected(world, pos, forPlayer) is { } chosen ? chosen.Entity.Info(chosen.Half) : "";
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        Seraph.Client.TooltipText.AppendItem(dsc, LocalizedText.Get("vsslssmod:filter-item-" + (ItemKind == TreatmentKind.Drain ? "drainpot" : "filterpot")));
    }
    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        var chosen = Selected(world, pos, world is Vintagestory.API.Client.IClientWorldAccessor c ? c.Player : null);
        return new ItemStack(world.GetBlock(new AssetLocation("vsslssmod:" +
            (chosen?.Entity.Slots[chosen.Value.Half].Kind == TreatmentKind.Drain ? "drainpot" : "filterpot"))));
    }
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.Side != EnumAppSide.Server || Selected(world, pos, byPlayer) is not { } chosen) return;
        if (byPlayer is not null)
        {
            if (!world.Claims.TryAccess(byPlayer, chosen.Entity.Pos, EnumBlockAccessFlags.BuildOrBreak)) return;
            var s = chosen.Entity.Slots[chosen.Half];
            if (s.Kind == TreatmentKind.Drain && !world.Claims.TryAccess(byPlayer,
                PipePos(chosen.Entity.Pos, s.Direction), EnumBlockAccessFlags.BuildOrBreak)) return;
        }
        chosen.Entity.Remove(chosen.Half, byPlayer?.WorldData.CurrentGameMode != EnumGameMode.Creative);
    }
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        => world.BlockAccessor.GetBlockEntity(pos) is BlockEntityWaterTreatment entity ? entity.AllDrops() : [];
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        if (world.Side == EnumAppSide.Server && world.BlockAccessor.GetBlockEntity(pos) is BlockEntityWaterTreatment entity)
            entity.RemoveLinks();
        base.OnBlockRemoved(world, pos);
    }
}
