using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;

namespace Seraph.Client;

public sealed partial class SeraphClientBridge
{
    private long nativeWorkSequence;
    private NativeWorkReceipt? pendingMining;
    private long lastMiningFlush;

    internal void AcceptedMining(EntityPlayer entity, BlockSelection selection, double seconds, int blockId)
    {
        if (!HasFreshSnapshot || !ReferenceEquals(entity, api.World.Player?.Entity) || !double.IsFinite(seconds) || seconds <= 0) return;
        PredictWork(entity, entity.Player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible.Tool == EnumTool.Axe
            ? ExertionKind.Logging : ExertionKind.Mining, seconds);
        var pos = selection.Position;
        var tool = entity.Player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible.Id ?? 0;
        if (pendingMining is { } old && (old.X != pos.X || old.Y != pos.Y || old.Z != pos.Z
            || old.Dimension != pos.dimension || old.ToolId != tool || old.BlockId != blockId)) FlushMining();
        pendingMining ??= new() { X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension, ToolId = tool, BlockId = blockId };
        pendingMining.Seconds = Math.Min(.5, pendingMining.Seconds + seconds);
        if (api.World.ElapsedMilliseconds - lastMiningFlush >= 100) FlushMining();
    }
    internal void AcceptedAttack(EntityPlayer entity)
    {
        if (!HasFreshSnapshot || !ReferenceEquals(entity, api.World.Player?.Entity)) return;
        PredictWork(entity, ExertionKind.Attack, 1);
        channel.SendPacket(new NativeWorkReceipt { SessionEpoch = Snapshot!.SessionEpoch, Sequence = ++nativeWorkSequence,
            Life = predictionLife, Attack = true, Seconds = .25, ToolId = entity.Player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible.Id ?? 0 });
    }
    private void FlushMining(bool force = true)
    {
        if (pendingMining is not { } receipt) return;
        if (!force && api.World.ElapsedMilliseconds - lastMiningFlush < 100) return;
        pendingMining = null;
        if (!HasFreshSnapshot) return;
        receipt.SessionEpoch = Snapshot!.SessionEpoch; receipt.Life = predictionLife; receipt.Sequence = ++nativeWorkSequence;
        channel.SendPacket(receipt);
        lastMiningFlush = api.World.ElapsedMilliseconds;
    }
}
