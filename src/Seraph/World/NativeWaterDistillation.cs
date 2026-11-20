using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

internal sealed class NativeWaterDistillation : IDisposable
{
    private const string Id = "vsslssmod.distillation";
    private static Harmony? harmony;
    private static int owners;
    private static readonly System.Reflection.FieldInfo VisualStack = AccessTools.Field(typeof(BlockEntityCondenser), "lastReceivedDistillate");
    private static readonly System.Reflection.FieldInfo VisualTime = AccessTools.Field(typeof(BlockEntityCondenser), "lastReceivedDistillateTotalMs");
    internal NativeWaterDistillation()
    {
        if (owners++ != 0) return;
        harmony = new(Id);
        harmony.Patch(AccessTools.Method(typeof(BlockEntityCondenser), nameof(BlockEntityCondenser.ReceiveDistillate)),
            prefix: new HarmonyMethod(typeof(NativeWaterDistillation), nameof(Receive)));
        harmony.Patch(AccessTools.Method(typeof(BlockEntityBoiler), nameof(BlockEntityBoiler.OnInteract)),
            prefix: new HarmonyMethod(typeof(NativeWaterDistillation), nameof(BoilerInteract)));
        harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.OnPlayerRightClick)),
            prefix: new HarmonyMethod(typeof(NativeWaterDistillation), nameof(FirepitInteract)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Block), nameof(Block.GetDrops)),
            postfix: new HarmonyMethod(typeof(NativeWaterDistillation), nameof(Drops)));
    }
    private static bool Enabled(ICoreAPI api) => api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig.Mode == "survival";
    private static bool Receive(BlockEntityCondenser __instance, ItemSlot sourceSlot, ref bool __result)
    {
        if (!WaterQuality.IsWater(sourceSlot.Itemstack) || !Enabled(__instance.Api)) return true;
        __result = false;
        // Native callers attempt distillation from 75 C; water requires actual boiling.
        var world = __instance.Api.World;
        var source = sourceSlot.Itemstack!;
        var temp = source.Collectible.GetTemperature(world, source);
        if (__instance.Api.Side != EnumAppSide.Server)
        {
            if (temp >= 100 && !__instance.Inventory[1].Empty && world.GetItem(new AssetLocation("game:waterportion")) is { } water)
            {
                VisualStack.SetValue(__instance, new ItemStack(water));
                VisualTime.SetValue(__instance, world.ElapsedMilliseconds);
            }
            return false;
        }
        if (temp < 100 || source.StackSize < 1
            || sourceSlot.Inventory?.Pos is not { } boilerPos
            || world.BlockAccessor.GetBlockEntity(boilerPos)?.GetBehavior<SaltResidueBehavior>() is not { } salt
            || __instance.Inventory[1].Itemstack is not { Collectible: BlockLiquidContainerTopOpened container } vessel) return false;
        var config = __instance.Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig;
        var batch = WaterQuality.ReadLiquidAt(world, source, temp, config);
        var outputItem = world.GetItem(new AssetLocation("game:waterportion"));
        if (batch is null || outputItem is null) return false;
        var inputUnits = BlockLiquidContainerBase.GetContainableProps(source)?.ItemsPerLitre ?? 0;
        var output = new ItemStack(outputItem, 1);
        var outputUnits = BlockLiquidContainerBase.GetContainableProps(output)?.ItemsPerLitre ?? 0;
        if (!float.IsFinite(inputUnits) || inputUnits <= 0 || outputUnits != inputUnits) return false;
        var (remaining, distilled, residue) = WaterDistillation.Separate(WaterHeating.AtTemperature(batch, temp), 1d / inputUnits);
        WaterMetadata.WriteAt(world, output, distilled);
        // Keep vanilla's slower air cooling and cooling-water use, but only charge accepted condensate.
        if (!container.IsEmpty(vessel) && __instance.Inventory[0].Empty && world.Rand.NextDouble() <= .5) return false;
        var before = vessel.Clone();
        var moved = container.TryPutLiquid(vessel, output, 1f / inputUnits);
        if (moved != 1) { __instance.Inventory[1].Itemstack = before; return false; }
        sourceSlot.TakeOut(1);
        if (!sourceSlot.Empty) WaterMetadata.WriteAt(world, sourceSlot.Itemstack, remaining);
        salt.Grams += residue;
        if (!__instance.Inventory[0].Empty && world.Rand.NextDouble() < .5) __instance.Inventory[0].TakeOut(1);
        sourceSlot.MarkDirty(); __instance.Inventory[1].MarkDirty(); salt.Blockentity.MarkDirty(); __instance.MarkDirty();
        __result = true;
        return false;
    }
    private static bool BoilerInteract(BlockEntityBoiler __instance, IPlayer byPlayer, ref bool __result)
    {
        if (!Enabled(__instance.Api) || !byPlayer.InventoryManager.ActiveHotbarSlot.Empty
            || __instance.GetBehavior<SaltResidueBehavior>() is not { Grams: >= 20 } salt) return true;
        __result = true;
        if (__instance.Api.Side == EnumAppSide.Server) {
            salt.Grams -= GiveSalt(__instance.Api.World, byPlayer, salt.Grams);
            __instance.MarkDirty();
        }
        return false;
    }
    private static bool FirepitInteract(BlockEntityFirepit __instance, IPlayer byPlayer, ref bool __result)
    {
        if (!Enabled(__instance.Api) || !byPlayer.InventoryManager.ActiveHotbarSlot.Empty || !byPlayer.Entity.Controls.ShiftKey
            || __instance.inputStack is not { } vessel || vessel.Attributes.GetDouble(SaltResidueBehavior.Key) < 20) return true;
        __result = true;
        if (__instance.Api.Side == EnumAppSide.Server) {
            var grams = vessel.Attributes.GetDouble(SaltResidueBehavior.Key);
            vessel.Attributes.SetDouble(SaltResidueBehavior.Key, grams - GiveSalt(__instance.Api.World, byPlayer, grams));
            __instance.inputSlot.MarkDirty();
        }
        return false;
    }
    private static double GiveSalt(IWorldAccessor world, IPlayer player, double grams)
    {
        var item = world.GetItem(new AssetLocation("vsslssmod:seasalt"));
        if (item is null || !double.IsFinite(grams)) return 0;
        var count = (int)Math.Min(64, Math.Floor(grams / 20 + 1e-9));
        if (count <= 0) return 0;
        var stack = new ItemStack(item, count);
        var all = player.InventoryManager.TryGiveItemstack(stack);
        return (all ? count : Math.Clamp(count - stack.StackSize, 0, count)) * 20d;
    }
    private static void Drops(Block __instance, IWorldAccessor world, BlockPos pos, ItemStack[] __result)
    {
        if (__instance is not BlockBoiler || world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<SaltResidueBehavior>() is not { Grams: > 0 } salt) return;
        var drop = __result.FirstOrDefault(s => s.Collectible is BlockBoiler);
        if (drop is not null) drop.Attributes.SetDouble(SaltResidueBehavior.Key, salt.Grams);
    }
    public void Dispose() { if (--owners == 0) { harmony?.UnpatchAll(Id); harmony = null; } }
}
