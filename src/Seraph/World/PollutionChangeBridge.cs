using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Invalidate source snapshots on container edits and moving dropped waste.</summary>
internal sealed class PollutionChangeBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.pollution-changes");
    internal PollutionChangeBridge()
    {
        harmony.Patch(AccessTools.Method(typeof(BlockEntity), nameof(BlockEntity.MarkDirty)),
            postfix: new(typeof(PollutionChangeBridge), nameof(Dirty)));
        harmony.Patch(AccessTools.Method(typeof(EntityItem), nameof(EntityItem.OnGameTick)),
            prefix: new(typeof(PollutionChangeBridge), nameof(BeforeMove)),
            postfix: new(typeof(PollutionChangeBridge), nameof(AfterMove)));
    }
    private static void Dirty(BlockEntity __instance)
    {
        if (__instance.Api?.Side == EnumAppSide.Server && __instance is BlockEntityContainer)
            WaterQuality.Invalidate(__instance.Api.World, __instance.Pos);
    }
    private static void BeforeMove(EntityItem __instance, out BlockPos? __state)
        => __state = __instance.World?.Side == EnumAppSide.Server ? __instance.Pos.AsBlockPos : null;
    private static void AfterMove(EntityItem __instance, BlockPos? __state)
    {
        if (__state is null || __instance.Pos.AsBlockPos.Equals(__state)
            || !LatrineWasteStorage.IsManure(__instance.Itemstack)
                && OrganicPollutionSource.DecayQuantity(__instance.World, __instance.Itemstack) <= 0) return;
        WaterQuality.Invalidate(__instance.World, __state);
        WaterQuality.Invalidate(__instance.World, __instance.Pos.AsBlockPos);
    }
    public void Dispose() => harmony.UnpatchAll(harmony.Id);
}
