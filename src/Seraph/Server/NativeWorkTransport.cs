using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private void RecordNativeBlockBreak(IServerPlayer player, int oldId, BlockSelection selection)
    {
        if (player is null || !Current(player, out var s)) return;
        foreach (var pos in s.RecentBrokenBlocks.Where(x => ServerSeconds - x.Value.At > 1).Select(x => x.Key).ToArray())
            s.RecentBrokenBlocks.Remove(pos);
        if (s.RecentBrokenBlocks.Count >= 32) s.RecentBrokenBlocks.Clear();
        s.RecentBrokenBlocks[selection.Position.Copy()] = (oldId, ServerSeconds);
    }

    private void OnNativeWork(IServerPlayer player, NativeWorkReceipt receipt)
    {
        if (!ReadyForAction(player, out var s) || receipt.SessionEpoch != s.Epoch || receipt.Life != s.ExertionLife || !OwnsPhysiology(player.Entity)) return;
        var e = player.Entity;
        if (player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible.Id != (receipt.ToolId == 0 ? null : receipt.ToolId)) return;
        if (ActionFunctions(e) is not { Manipulation: true, CanUseActiveHand: true }) { DeniedLabor(e); return; }
        if (!receipt.Attack)
        {
            var pos = new BlockPos(receipt.X, receipt.Y, receipt.Z, receipt.Dimension);
            if (pos.dimension != e.Pos.Dimension || e.Pos.SquareDistanceTo(pos.ToVec3d().Add(.5, .5, .5)) > 36
                || api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null
                || !api.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak)) return;
            var block = api.World.BlockAccessor.GetBlock(pos);
            // The final productive frame can arrive just after vanilla removed the target.
            if (block.Id != receipt.BlockId && !(block.Id == 0 && s.LastMiningTarget?.Equals(pos) == true
                && ServerSeconds - s.LastMiningReceipt < .5)
                && !(block.Id == 0 && s.RecentBrokenBlocks.TryGetValue(pos, out var broken)
                    && broken.Id == receipt.BlockId && ServerSeconds - broken.At <= 1)) return;
            if (block.Id == 0 && receipt.BlockId == 0) return;
            s.LastMiningTarget = pos; s.LastMiningReceipt = ServerSeconds;
        }
        var seconds = s.NativeWork.Accept(receipt.Sequence, ServerSeconds, receipt.Seconds, receipt.Attack);
        if (seconds <= 0) return;
        if (receipt.Attack) ChargeAction(e, ExertionKind.Attack);
        else MarkMining(e, seconds);
    }
}
