using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Water accounting at Cottage Industry 1.2.3's custom vat boundaries.
/// Native code still owns capacity, vessel splitting, recipes, taps and persistence.</summary>
internal sealed class CottageWaterCompatibility : IDisposable
{
    private const string PatchId = "vsslssmod.cottagewater";
    private static Harmony? harmony;
    private static Binding? binding;
    private static int owners;
    [ThreadStatic] private static Transfer? active;
    private readonly bool attached;

    private sealed class Binding(Type vat)
    {
        internal readonly FieldInfo Contents = AccessTools.Field(vat, "C") ?? throw new MissingFieldException("vat.C");
        internal readonly FieldInfo Juice = AccessTools.Field(vat.Assembly.GetType("CottageIndustry.FruitContents", true), "Juice")
            ?? throw new MissingFieldException("FruitContents.Juice");
        internal readonly MethodInfo Changed = AccessTools.Method(vat, "SoakChanged", []) ?? throw new MissingMethodException("SoakChanged");
        internal ItemStack? Get(BlockEntity entity) => (ItemStack?)Juice.GetValue(Contents.GetValue(entity));
        internal void Set(BlockEntity entity, ItemStack stack) => Juice.SetValue(Contents.GetValue(entity), stack);
    }

    private sealed class Transfer(BlockEntity vat, ItemStack? before, WaterBatch? existing, WaterBatch incoming)
    {
        internal readonly Transfer? Previous = active;
        internal readonly BlockEntity Vat = vat;
        internal readonly ItemStack? Before = before;
        internal readonly WaterBatch? Existing = existing;
        internal readonly WaterBatch Incoming = incoming;
        internal bool Committed;
    }

    internal CottageWaterCompatibility(ICoreAPI api)
    {
        var mod = api.ModLoader.GetMod("cottageindustry");
        if (mod is null) return;
        if (mod.Info.Version != "1.2.3")
        {
            api.Logger.Warning("[vsslssmod] Cottage Industry water adapter supports 1.2.3; found {0}.", mod.Info.Version);
            return;
        }
        var vat = AccessTools.TypeByName("CottageIndustry.BETreadingVat");
        if (vat is null) return;
        Attach(vat);
        attached = true;
        api.Logger.Notification("[vsslssmod] Cottage Industry 1.2.3 vat water compatibility active.");
    }

    // Tests bind the installed mod assembly, not a replacement implementation.
    internal CottageWaterCompatibility(Type vat) { Attach(vat); attached = true; }

    private static void Attach(Type vat)
    {
        if (owners++ > 0) return;
        harmony = new Harmony(PatchId);
        try
        {
            binding = new Binding(vat);
            if (binding.Juice.FieldType != typeof(ItemStack)) throw new InvalidOperationException("Unsupported vat liquid field.");
            Patch("SoakInteract", [typeof(IPlayer), typeof(ItemSlot)], nameof(BeginHeld), true);
            Patch("AcceptPoured", [typeof(ItemStack), typeof(float)], nameof(BeginPoured), true);
            Patch("AddRain", [typeof(double)], nameof(BeginRain), false);
            harmony.Patch(binding.Changed, prefix: new HarmonyMethod(typeof(CottageWaterCompatibility), nameof(BeforeChanged)));
        }
        catch { harmony.UnpatchAll(PatchId); harmony = null; binding = null; owners = 0; throw; }

        void Patch(string name, Type[] parameters, string prefix, bool compare)
        {
            var method = AccessTools.DeclaredMethod(vat, name, parameters) ?? throw new MissingMethodException(vat.FullName, name);
            harmony!.Patch(method, prefix: new HarmonyMethod(typeof(CottageWaterCompatibility), prefix),
                transpiler: compare ? new HarmonyMethod(typeof(CottageWaterCompatibility), nameof(CompareWater)) : null,
                finalizer: new HarmonyMethod(typeof(CottageWaterCompatibility), nameof(EndTransfer)));
        }
    }

    private static bool Enabled(BlockEntity vat) => vat.Api is { } api && (api.Side == EnumAppSide.Client
        || api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig.Mode == "survival");

    private static bool BeginHeld(BlockEntity __instance, IPlayer player, ItemSlot slot, ref bool __result, out Transfer? __state)
    {
        __state = null;
        if (!Enabled(__instance) || slot?.Itemstack?.Collectible is not BlockLiquidContainerBase container) return true;
        var source = container.GetContent(slot.Itemstack);
        if (!WaterQuality.IsWater(source) && !(source is null && WaterQuality.IsWater(binding!.Get(__instance)))) return true;
        if (!__instance.Api.World.Claims.TryAccess(player, __instance.Pos, EnumBlockAccessFlags.Use))
        { __result = false; return false; }
        if (source is null) return true; // Empty vessels retain the native extraction path.
        if (Begin(__instance, source!, out __state)) return true;
        __result = true; // Reject invalid metadata before native code can consume the vessel.
        return false;
    }

    private static bool BeginPoured(BlockEntity __instance, ItemStack liquid, ref float litres, ref int __result, out Transfer? __state)
    {
        __state = null;
        if (!Enabled(__instance) || !WaterQuality.IsWater(liquid)) return true;
        __result = 0;
        var units = BlockLiquidContainerBase.GetContainableProps(liquid)?.ItemsPerLitre ?? 0;
        if (__instance.Api.Side != EnumAppSide.Server || !float.IsFinite(litres) || litres <= 0
            || !float.IsFinite(units) || units <= 0 || liquid.StackSize <= 0) return false;
        // Cottage's public stream endpoint trusts the requested litres. Bound it by
        // the actual source stack so a partial upstream reservoir cannot duplicate water.
        litres = Math.Min(litres, liquid.StackSize / units);
        return Begin(__instance, liquid, out __state);
    }

    private static bool BeginRain(BlockEntity __instance, double litres, out Transfer? __state)
    {
        __state = null;
        if (!Enabled(__instance)) return true;
        var current = binding!.Get(__instance);
        if (current is not null && !WaterQuality.IsWater(current)) return true; // Native dye/tannin dilution.
        if (__instance.Api.Side != EnumAppSide.Server || !double.IsFinite(litres) || litres <= 0) return false;
        var item = __instance.Api.World.GetItem(new AssetLocation("game:waterportion"));
        if (item is null) return false;
        var source = new ItemStack(item);
        var units = BlockLiquidContainerBase.GetContainableProps(source)?.ItemsPerLitre ?? 0;
        var count = Math.Round(litres * units);
        if (!float.IsFinite(units) || units <= 0 || count <= 0 || count > int.MaxValue) return false;
        source.StackSize = (int)count;
        var config = __instance.Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig;
        WaterMetadata.Write(source, new WaterBatch { Source = "rain", VolumeL = count / units,
            TemperatureC = WaterQuality.Ambient(__instance.Api.World, __instance.Pos),
            EntericHazardPerL = config.RainWaterHazardPerL });
        return Begin(__instance, source, out __state);
    }

    private static bool Begin(BlockEntity vat, ItemStack source, out Transfer? state)
    {
        state = null;
        var current = binding!.Get(vat);
        if (current is not null && !WaterQuality.IsWater(current)) return true;
        try
        {
            var world = vat.Api.World;
            var config = vat.Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig;
            var ambient = WaterQuality.Ambient(world, vat.Pos);
            var incoming = WaterQuality.ReadLiquidAt(world, source, ambient, config);
            var existing = current is null ? null : WaterQuality.ReadLiquidAt(world, current, ambient, config);
            if (incoming is null || incoming.Validate() is not null || current is not null && (existing is null
                || existing.Validate() is not null
                || BlockLiquidContainerBase.GetContainableProps(current)?.ItemsPerLitre != BlockLiquidContainerBase.GetContainableProps(source)?.ItemsPerLitre)) return false;
            // Rain is a known fresh input, not another branded liquid. Preserve any
            // unrelated stored attributes when native rain collection dilutes the vat.
            if (current is not null && incoming.Source != "rain" && !NativeWaterTransfer.WaterIdentityMatches(world, current, source)) return true;
            if (vat.Api.Side == EnumAppSide.Client) return true;
            state = new Transfer(vat, current?.Clone(), existing, incoming);
            active = state;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    private static IEnumerable<CodeInstruction> CompareWater(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var equals = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Equals),
            [typeof(IWorldAccessor), typeof(ItemStack), typeof(string[])]);
        var comparisons = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(equals))
            {
                // Only the first comparison checks liquid. SoakInteract's second
                // comparison belongs to solid ingredients and must remain native.
                if (comparisons++ == 0)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(CottageWaterCompatibility), nameof(LiquidEquals));
                }
            }
            yield return instruction;
        }
        var expected = __originalMethod.Name == "SoakInteract" ? 2 : 1;
        if (comparisons != expected) throw new InvalidOperationException("Cottage Industry liquid comparison changed.");
    }

    private static bool LiquidEquals(ItemStack current, IWorldAccessor world, ItemStack incoming, string[] ignored)
    {
        if ((world.Side == EnumAppSide.Client || world.Api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig.Mode == "survival")
            && WaterQuality.IsWater(current) && WaterQuality.IsWater(incoming))
            return NativeWaterTransfer.WaterIdentityMatches(world, current, incoming);
        return current.Equals(world, incoming, ignored);
    }

    private static void BeforeChanged(BlockEntity __instance)
    {
        if (active is { } state && ReferenceEquals(state.Vat, __instance)) Commit(state);
    }

    private static void Commit(Transfer state)
    {
        if (state.Committed) return;
        state.Committed = true;
        var current = binding!.Get(state.Vat);
        var added = (current?.StackSize ?? 0) - (state.Before?.StackSize ?? 0);
        if (added <= 0 || !WaterQuality.IsWater(current)) return;
        var units = BlockLiquidContainerBase.GetContainableProps(current!)!.ItemsPerLitre;
        var incoming = state.Incoming with { VolumeL = added / (double)units };
        var mixed = state.Existing?.Mix(incoming) ?? incoming;
        var water = state.Vat.Api.World.GetItem(new AssetLocation(mixed.SaltGramsPerL > 5 ? "game:saltwaterportion" : "game:waterportion"))
            ?? throw new InvalidOperationException("Missing native water item.");
        var result = new ItemStack(water, current!.StackSize) { Attributes = current.Attributes.Clone() };
        result.Attributes.RemoveAttribute(NativeWaterHeating.RemainderKey);
        WaterMetadata.WriteAt(state.Vat.Api.World, result, mixed);
        binding.Set(state.Vat, result);
    }

    private static Exception? EndTransfer(Exception? __exception, Transfer? __state)
    {
        if (__state is null) return __exception;
        try
        {
            // Update even when native stream synchronization is throttled. A later
            // call must read the previous pour's mixture, not its original concentration.
            Commit(__state);
            return __exception;
        }
        finally { active = __state.Previous; }
    }

    public void Dispose()
    {
        if (!attached || --owners != 0) return;
        harmony?.UnpatchAll(PatchId); harmony = null; binding = null;
    }
}
