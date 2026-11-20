using Vintagestory.API.Common;
using Vintagestory.GameContent;
using Seraph.Core.Framework;

namespace Seraph.World;

internal static class OrganicPollutionSource
{
    internal static bool IsFantasy(AssetLocation code) => code.Domain == "game" && code.Path.Split('-')[0]
        is "drifter" or "shiver" or "bowtorn" or "bell" or "locust" or "eidolon" or "immobilizer";

    // The Dead mod represents a player's remains with a live entity object.
    // Identify that explicit asset instead of assuming every "dead*" entity is biological.
    internal static bool IsPlayerCorpse(AssetLocation code) => code.Domain == "dead"
        && (code.Path == "playercorpse" || code.Path.StartsWith("playercorpse-", StringComparison.Ordinal));
    internal static bool IsRot(ItemStack? stack)
    {
        var code = stack?.Collectible.Code;
        if (code is null) return false;
        if (code.Path is "rot" or "rottenfood") return true;
        // A rotten wood/stone asset is not food. Variant names alone are insufficient.
        return code.Path.Split('-').Any(p => p is "rotten" or "decayed")
            && stack!.Collectible.Attributes?["foodNutritionProps"].Exists == true;
    }
    internal static bool IsButcherCorpse(ItemStack? stack) => stack?.Collectible.Code is { Domain: "butchering" } code
        && code.Path.StartsWith("dead", StringComparison.Ordinal)
        && stack.Collectible.Attributes?["bloodType"].AsString() is not null;
    internal static bool IsButcherRemains(ItemStack? stack) => stack?.Collectible.Code is { Domain: "butchering" } code
        && (IsButcherCorpse(stack) || code.Path.Split('-')[0] is "blood" or "bloodportion" or "guts" or "entrails" or "offal");
    internal static OrganicWasteKind Kind(ItemStack? stack) => IsButcherCorpse(stack) ? OrganicWasteKind.Corpse
        : IsButcherRemains(stack) ? OrganicWasteKind.ButcherRemains : OrganicWasteKind.Rot;
    internal static WasteContainment Containment(BlockEntityContainer container) => container switch {
        BlockEntityBarrel barrel => barrel.Sealed ? WasteContainment.SealedWatertight : WasteContainment.OpenWatertight,
        BlockEntityGroundStorage => WasteContainment.Exposed,
        _ when container.Block?.Code is { Domain: "butchering" } code
            && (code.Path.StartsWith("butchertable-", StringComparison.Ordinal) || code.Path.StartsWith("butcherhook", StringComparison.Ordinal)
                || code.Path.StartsWith("meathook", StringComparison.Ordinal)) => WasteContainment.Exposed,
        _ when container.Block is BlockLiquidContainerBase => WasteContainment.OpenWatertight,
        _ => WasteContainment.ClosedStorage };

    internal static double DecayQuantity(IWorldAccessor world, ItemStack? stack)
    {
        if (stack is null || stack.StackSize <= 0) return 0;
        if (IsButcherRemains(stack)) return stack.StackSize * (stack.Collectible.Code.Path.EndsWith("-rotten", StringComparison.Ordinal) ? 8 : 2);
        if (IsRot(stack)) return stack.StackSize;
        // Fresh meat, fermentation and transition time alone are not decay.
        if (stack.Collectible.GetTransitionableProperties(world, stack, null)?.Any(p => p.Type == EnumTransitionType.Perish) != true) return 0;
        var fraction = stack.Collectible.UpdateAndGetTransitionState(world,
            new DummySlot(stack.Clone()), EnumTransitionType.Perish)?.TransitionLevel ?? 0;
        return float.IsFinite(fraction) ? stack.StackSize * Math.Clamp(fraction, 0, 1) : 0;
    }

    internal static bool TryCompost(BlockEntity entity, IWorldAccessor world, out double load, out bool aerated)
    {
        load = 0; aerated = false;
        if (entity.GetType().FullName != "CottageIndustry.BECompostBay") return false;
        var type = entity.GetType();
        var state = type.GetField("State")?.GetValue(entity) ?? type.GetProperty("State")?.GetValue(entity);
        if (state is null) return false;
        double Number(string name) => Convert.ToDouble(state.GetType().GetProperty(name)?.GetValue(state)
            ?? state.GetType().GetField(name)?.GetValue(state) ?? 0);
        var raw = Math.Max(0, Number("RawRot"));
        var green = Math.Max(0, Number("GreenRot"));
        var volume = Math.Max(0, Number("Volume"));
        var progress = Math.Clamp(Number("AverageProgress"), 0, 1);
        aerated = state.GetType().GetMethod("IsAerated")?.Invoke(state, [world.Calendar.TotalHours]) is true;
        // Actual raw rot, maturity and remaining heap volume; brown/finished compost
        // emits much less than wet green waste. No invented named pathogen.
        load = Math.Min(4, (raw + green * .5 + Math.Max(0, volume - raw) * .05) * .125 * (1 - .9 * progress));
        return load > 0;
    }
}
