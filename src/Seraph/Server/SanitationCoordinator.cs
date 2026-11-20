using HarmonyLib;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private SanitationContext ObserveSanitation(Session s, MicroclimateResult sample)
    {
        var entity = s.Player.Entity;
        var pos = entity.Pos.AsBlockPos;
        var ground = api.World.BlockAccessor.GetBlock(pos.DownCopy());
        var carries = new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName }
            .SelectMany(name => s.Player.InventoryManager.GetOwnInventory(name)?.Take(64) ?? [])
            .Any(slot => LatrineWasteStorage.IsManure(slot.Itemstack));
        var water = entity.Swimming ? WaterQuality.WorldSource(api.World, pos, settings.Config) : null;
        return new() {
            DirtyGround = entity.OnGround && ground.BlockMaterial is EnumBlockMaterial.Soil or EnumBlockMaterial.Sand or EnumBlockMaterial.Gravel,
            CarriesWaste = carries, Immersed = water is not null,
            WaterDirtiness = water is null ? 0 : Sanitation.WaterResidue(water),
            Raining = !sample.Sheltered && sample.Rainfall > .1 && sample.Environment.AirTemperatureC > 0,
            PollutionPoints = 500 * (sample.Environment.AirPollution + sample.Environment.AirbornePathogens)
        };
    }

    internal void RecordButchering(IPlayer player)
    {
        if (player is not IServerPlayer serverPlayer || !Current(serverPlayer, out var s)
            || !OwnsPhysiology(serverPlayer.Entity)) return;
        s.Framework = s.Framework with { Dirtiness = Math.Min(1, s.Framework.Dirtiness + .025 * Sanitation.GainMultiplier(s.Framework)) };
    }
}

internal sealed class NativeSanitationBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.sanitation");
    private static ServerCoordinator? coordinator;
    internal NativeSanitationBridge(ServerCoordinator server)
    {
        coordinator = server;
        harmony.Patch(AccessTools.Method(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.SetHarvested)),
            prefix: new(typeof(NativeSanitationBridge), nameof(Before)), postfix: new(typeof(NativeSanitationBridge), nameof(After)));
    }
    private static void Before(EntityBehaviorHarvestable __instance, out bool __state) => __state = __instance.IsHarvested;
    private static void After(EntityBehaviorHarvestable __instance, IPlayer byPlayer, bool __state)
    {
        if (!__state && __instance.IsHarvested) coordinator?.RecordButchering(byPlayer);
    }
    public void Dispose() { harmony.UnpatchAll("vsslssmod.sanitation"); coordinator = null; }
}
