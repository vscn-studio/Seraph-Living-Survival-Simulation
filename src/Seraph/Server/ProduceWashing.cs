using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private sealed record PendingProduceWash(double Elapsed, double X, double Y, double Z,
        long Generation, int HurtCounter, BlockPos Target, int SolidId, int FluidId,
        BlockEntity? Container, ItemSlot Slot, ItemStack Held, int Count);

    private TextCommandResult BeginProduceWash(Session s)
    {
        var e = s.Player.Entity;
        var slot = s.Player.InventoryManager.ActiveHotbarSlot;
        var target = WaterTarget(s);
        if (!ReadyForAction(s.Player, out _) || !e.ServerControls.ShiftKey || target is null
            || !NativeCropPollution.Washable(slot.Itemstack, api.World) || WashSource(s, target) is null)
            return TextCommandResult.Error(L(s.Player, "water-required"));
        if (e.ServerControls.TriesToMove || e.MountedOn is not null || e.Swimming)
            return TextCommandResult.Error(L(s.Player, "must-rest"));
        CancelCompetingActions(s);
        s.ProduceWash = new(0, e.Pos.X, e.Pos.Y, e.Pos.Z, s.LifeGeneration,
            e.WatchedAttributes.GetInt("onHurtCounter"), target.Copy(),
            api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Solid).Id,
            api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Fluid).Id,
            api.World.BlockAccessor.GetBlockEntity(target), slot, slot.Itemstack!, slot.StackSize);
        return TextCommandResult.Success(L(s.Player, "produce-wash-start"));
    }

    private void TickProduceWash(Session s, bool advancing, double step)
    {
        if (s.ProduceWash is not { } p) return;
        var accessor = api.World.BlockAccessor;
        if (ServerSeconds - s.LastDrinkHoldSeconds > .75 || !s.Player.Entity.ServerControls.ShiftKey
            || ActionInterrupted(s, advancing, p.X, p.Y, p.Z, p.Generation, p.HurtCounter)
            || WaterTarget(s)?.Equals(p.Target) != true
            || accessor.GetBlock(p.Target, BlockLayersAccess.Solid).Id != p.SolidId
            || accessor.GetBlock(p.Target, BlockLayersAccess.Fluid).Id != p.FluidId
            || !ReferenceEquals(accessor.GetBlockEntity(p.Target), p.Container)
            || !ReferenceEquals(s.Player.InventoryManager.ActiveHotbarSlot, p.Slot)
            || !ReferenceEquals(p.Slot.Itemstack, p.Held) || p.Slot.StackSize != p.Count)
        { s.ProduceWash = null; Notify(s, "world-cancelled"); return; }
        s.ProduceWash = p = p with { Elapsed = p.Elapsed + step };
        if (p.Elapsed < 3) return;
        s.ProduceWash = null; // Completion is claimed before attempting any mutation.
        var source = WashSource(s, p.Target);
        if (source is null) { Notify(s, "water-required"); return; }
        if (source.Batch.VolumeL + 1e-9 < p.Count * .25) { Notify(s, "produce-wash-water"); return; }
        if (!CommitProduceWash(s, p.Slot, source)) Notify(s, "action-save-failed");
        else Notify(s, "produce-wash-complete");
        SendSnapshot(s);
    }

    private bool CommitProduceWash(Session s, ItemSlot held, WaterSource source)
    {
        if (held.Itemstack is not { StackSize: > 0 } input) return false;
        var waterNeeded = input.StackSize * .25;
        if (source.Batch.VolumeL + 1e-9 < waterNeeded) return false;
        var original = input.Clone();
        var washed = input.Clone();
        var waterAgents = source.Batch.PathogensPerL;
        if (source.Batch.EntericHazardPerL > 0)
            waterAgents = PathogenLoads.WithGenericEnteric(waterAgents, source.Batch.EntericHazardPerL);
        var oldFrame = s.Framework;
        var handTransfer = HandContamination.TransferToFood(oldFrame, settings.Config.HandToFoodTransferFraction);
        PathogenMetadata.Write(washed, PathogenLoads.Add(PathogenLoads.Scale(PathogenMetadata.Read(input), .1),
            PathogenLoads.Add(PathogenLoads.Scale(waterAgents, .25), handTransfer)));
        try
        {
            if (!source.Take(waterNeeded)) { source.Restore(); return false; }
            held.Itemstack = washed;
            s.Framework = HandContamination.AfterWashingProduce(oldFrame, source.Batch,
                cleanWaterRetention: settings.Config.CleanWaterHandRetention,
                dirtyWaterRetention: settings.Config.DirtyWaterHandRetention);
            held.MarkDirty();
            if (!Save(s)) throw new InvalidOperationException("Produce washing checkpoint rejected.");
            return true;
        }
        catch
        {
            held.Itemstack = original;
            s.Framework = oldFrame;
            held.MarkDirty(); source.Restore();
            return false;
        }
    }
}
