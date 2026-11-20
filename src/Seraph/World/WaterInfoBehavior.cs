using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;

namespace Seraph.World;

/// <summary>Extends the game's existing block information overlay; never creates a second window.</summary>
internal sealed class WaterInfoBehavior(Block block) : BlockBehavior(block)
{
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        if (world.Api is not ICoreClientAPI api) return "";
        var clientPlayer = api.World.Player;
        var selection = clientPlayer?.CurrentBlockSelection;
        var waterCell = forPlayer.Entity is null ? null
            : WaterInteractionTarget.FindForHover(world, forPlayer.Entity, selection,
                clientPlayer?.WorldData?.PickingRange ?? 6);
        if (waterCell is null) return "";
        var bridge = api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge;
        return (bridge?.WaterInfo(waterCell) ?? LocalizedText.Get("vsslssmod:water-hover-pending")) + "\n";
    }
}
