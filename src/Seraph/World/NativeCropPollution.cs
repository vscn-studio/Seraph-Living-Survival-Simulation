using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Uses cached regional values inside native growth; native nutrients and moisture still own growth.</summary>
internal sealed class NativeCropPollution : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.crops");
    private sealed class Cursor { internal double Seconds; }
    private static readonly ConditionalWeakTable<BlockEntityFarmland, Cursor> Cursors = new();
    internal NativeCropPollution()
    {
        harmony.Patch(AccessTools.Method(typeof(BlockEntityFarmland), "GetHoursForNextStage"),
            postfix: new(typeof(NativeCropPollution), nameof(Growth)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityFarmland), "beginIntervalledUpdate"),
            postfix: new(typeof(NativeCropPollution), nameof(Interval)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockCrop), "GetDrops"),
            postfix: new(typeof(NativeCropPollution), nameof(Harvest)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityFarmland), "OnBlockUnloaded"),
            postfix: new(typeof(NativeCropPollution), nameof(Unloaded)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityFarmland), "OnCropBlockBroken"),
            postfix: new(typeof(NativeCropPollution), nameof(Clear)));
    }
    private static void Growth(BlockEntityFarmland __instance, ref double __result)
    {
        // Regional chemistry is no longer an extra crop-growth modifier. Native
        // soil moisture and fertilizer rules remain the sole growth inputs.
    }
    private static void Interval(BlockEntityFarmland __instance, ref FarmlandFastForwardUpdate onInterval)
    {
        if (__instance.Api?.Side != EnumAppSide.Server) return;
        var original = onInterval;
        var now = WorldCalendarTime.Seconds(__instance.Api.World);
        var cursor = Cursors.GetValue(__instance, _ => new() { Seconds = now });
        var elapsed = Math.Max(0, now - cursor.Seconds);
        cursor.Seconds = now;
        // Native catch-up can contain offline intervals. Earn contamination only during observed loaded time.
        onInterval = (hours, climate, light, paused) => {
            if (!paused && elapsed > 0 && __instance.GetCrop() is not null)
            {
                var dt = Math.Min(elapsed, Math.Max(0, hours) / __instance.Api.World.Calendar.HoursPerDay * 86400);
                elapsed -= dt;
                var region = WaterQuality.RegionAt(__instance.Api.World, __instance.Pos);
                Write(__instance, CropSurfaceContamination.Advance(Read(__instance), region, dt));
            }
            original(hours, climate, light, paused);
        };
    }
    private static IReadOnlyDictionary<string, double> Read(BlockEntityFarmland soil)
    {
        try {
            var json = soil.CropAttributes.GetString(PathogenMetadata.Key, "{}");
            if (json.Length > 4096) return PathogenLoads.Empty;
            var map = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            return PathogenLoads.Valid(map) ? map! : PathogenLoads.Empty;
        } catch (JsonException) { return PathogenLoads.Empty; }
    }
    internal static IReadOnlyDictionary<string, double> ReadForInfo(BlockEntityFarmland soil) => Read(soil);
    internal static string InfoLevel(double total) => !double.IsFinite(total) || total <= 0 ? "crop-pollution-clean"
        : total < 0.1 ? "crop-pollution-low" : total < 0.35 ? "crop-pollution-moderate" : "crop-pollution-high";
    private static void Write(BlockEntityFarmland soil, IReadOnlyDictionary<string, double> map)
    {
        soil.CropAttributes.SetString(PathogenMetadata.Key, JsonSerializer.Serialize(map));
        // CropAttributes are part of the farmland entity tree; dirty it so clients
        // receive the same accumulated load that is attached to harvested food.
        soil.MarkDirty();
    }
    private static void Harvest(IWorldAccessor world, Vintagestory.API.MathTools.BlockPos pos, ItemStack[] __result)
    {
        if (world.Side != EnumAppSide.Server || world.BlockAccessor.GetBlockEntity(pos.DownCopy()) is not BlockEntityFarmland soil) return;
        var agents = Read(soil);
        foreach (var stack in __result ?? [])
            if (Edible(stack, world)) PathogenMetadata.Write(stack, PathogenLoads.Add(PathogenMetadata.Read(stack), agents));
    }
    internal static bool Edible(ItemStack? stack, IWorldAccessor world) => stack is not null
        && !stack.Collectible.Code.Path.StartsWith("seeds-", StringComparison.Ordinal)
        && stack.Collectible.GetNutritionProperties(world, stack, null) is not null;
    internal static bool Washable(ItemStack? stack, IWorldAccessor world) => Edible(stack, world)
        && stack!.Collectible.GetNutritionProperties(world, stack, null)?.FoodCategory is EnumFoodCategory.Fruit or EnumFoodCategory.Vegetable;
    private static void Unloaded(BlockEntityFarmland __instance) => Cursors.Remove(__instance);
    private static void Clear(BlockEntityFarmland __instance)
    { __instance.CropAttributes.RemoveAttribute(PathogenMetadata.Key); Cursors.Remove(__instance); }
    public void Dispose() => harmony.UnpatchAll("vsslssmod.crops");
}
