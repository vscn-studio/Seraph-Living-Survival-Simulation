using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

internal sealed class NativeWaterHeating : IDisposable
{
    private const string PatchId = "vsslssmod.waterheating";
    internal const string RemainderKey = "vsslssmod:evaporationUnits";
    private static int owners;
    private static Harmony? harmony;

    public NativeWaterHeating()
    {
        if (owners++ != 0) return;
        harmony = new(PatchId);
        harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), "canHeatInput"),
            postfix: new HarmonyMethod(typeof(NativeWaterHeating), nameof(CanHeat)));
        harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), "heatInput"),
            prefix: new HarmonyMethod(typeof(NativeWaterHeating), nameof(Heat)));
        harmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.SetTemperature)),
            postfix: new HarmonyMethod(typeof(NativeWaterHeating), nameof(Temperature)));
    }

    public void Dispose()
    {
        if (--owners == 0) { harmony?.UnpatchAll(PatchId); harmony = null; }
    }

    private static bool Vessel(ItemStack? stack, out BlockLiquidContainerBase container, out ItemStack liquid)
    {
        container = stack?.Collectible as BlockLiquidContainerBase ?? null!;
        liquid = container?.GetContent(stack!)!;
        return container is not null && WaterQuality.IsWater(liquid);
    }

    private static bool Enabled(IWorldAccessor world) => world.Side == EnumAppSide.Server
        && world.Api.ModLoader.GetModSystem<SeraphModSystem>()?.InteractionConfig.Mode == "survival";

    private static void CanHeat(BlockEntityFirepit __instance, ref bool __result)
    {
        if (!__result && (__instance.Api.Side == EnumAppSide.Client || Enabled(__instance.Api.World))
            && Vessel(__instance.inputStack, out _, out _)) __result = true;
    }

    private static bool Heat(BlockEntityFirepit __instance, float dt)
    {
        if (!Enabled(__instance.Api.World) || !float.IsFinite(dt) || dt <= 0
            || !Vessel(__instance.inputStack, out var container, out var liquid)) return true;
        var world = __instance.Api.World;
        var water = WaterQuality.ReadLiquidAt(world, liquid, 20, world.Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig);
        if (water is null) return true;
        var (heated, evaporated) = WaterHeating.Advance(water, __instance.furnaceTemperature, dt);
        var unitsPerLitre = BlockLiquidContainerBase.GetContainableProps(liquid)!.ItemsPerLitre;
        var units = evaporated * unitsPerLitre + liquid.Attributes.GetDouble(RemainderKey);
        var removed = Math.Min(liquid.StackSize, (int)Math.Floor(units + 1e-9));
        liquid.Attributes.SetDouble(RemainderKey, units - removed);
        var actual = WaterHeating.Evaporate(WaterHeating.AtTemperature(water, heated.TemperatureC), removed / (double)unitsPerLitre);
        var salt = Math.Max(0, water.VolumeL * water.SaltGramsPerL - actual.VolumeL * actual.SaltGramsPerL);
        if (salt > 0) __instance.inputStack.Attributes.SetDouble(SaltResidueBehavior.Key,
            __instance.inputStack.Attributes.GetDouble(SaltResidueBehavior.Key) + salt);
        liquid.StackSize -= removed;
        // The native firepit reads the vessel's temperature for its own display.
        __instance.inputStack.Collectible.SetTemperature(world, __instance.inputStack, (float)actual.TemperatureC);
        if (liquid.StackSize <= 0) container.SetContent(__instance.inputStack, null!);
        else
        {
            liquid.Collectible.SetTemperature(world, liquid, (float)actual.TemperatureC);
            WaterMetadata.WriteAt(world, liquid, actual);
            container.SetContent(__instance.inputStack, liquid);
        }
        __instance.inputSlot.MarkDirty();
        return false;
    }

    private static void Temperature(IWorldAccessor world, ItemStack itemstack, float temperature)
    {
        if (!float.IsFinite(temperature)) return;
        ItemStack? liquid = WaterQuality.IsWater(itemstack) ? itemstack
            : Vessel(itemstack, out _, out var content) ? content : null;
        if (liquid is null || !Enabled(world)) return;
        var water = WaterQuality.ReadLiquidAt(world, liquid, temperature, world.Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig);
        if (water is null) return;
        // This is the SetTemperature postfix itself; write metadata directly so
        // the callback cannot recursively call SetTemperature.
        WaterMetadata.Write(liquid, WaterHeating.AtTemperature(water, temperature));
        WaterMetadata.StampTemperature(world, liquid);
        if (!ReferenceEquals(liquid, itemstack) && itemstack.Collectible is BlockLiquidContainerBase container)
        {
            liquid.Collectible.SetTemperature(world, liquid, temperature);
            container.SetContent(itemstack, liquid);
        }
    }
}
