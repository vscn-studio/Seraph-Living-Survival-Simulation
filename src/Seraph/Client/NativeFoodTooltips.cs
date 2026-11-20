using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Seraph.Client;

/// <summary>Remove only verified vanilla satiety/category projections; preserve health and other mod text.</summary>
internal sealed class NativeFoodTooltips : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.foodtooltips");
    internal NativeFoodTooltips()
    {
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo)),
            postfix: new HarmonyMethod(typeof(NativeFoodTooltips), nameof(StripBase)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionFacts),
            [typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float)]),
            postfix: new HarmonyMethod(typeof(NativeFoodTooltips), nameof(StripMeal)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleBehaviorHealingItem), nameof(CollectibleBehaviorHealingItem.GetHeldItemInfo)),
            postfix: new HarmonyMethod(typeof(NativeFoodTooltips), nameof(BandageInfo)));
    }
    private static bool Ready(IWorldAccessor world) => world.Side == EnumAppSide.Client
        && world.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge?.Foods is not null;
    private static void BandageInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        if (!TooltipText.IsMainThread || world.Side != EnumAppSide.Client || inSlot.Itemstack?.Collectible.Code is not { Domain: "game" } code
            || code.Path is not ("bandage-clean" or "bandage-alcoholed")) return;
        var bridge = world.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge;
        if (bridge is { HasFreshSnapshot: true, Snapshot.OwnWoundCare: true })
            TooltipText.AppendItem(dsc, Lang.Get("vsslssmod:medical-bandage-item"));
    }
    private static string Pattern(string key, params object[] args) => "^" + Regex.Escape(Lang.Get(key, args))
        .Replace("\\{0}", "(?<first>[-+0-9.,]+)").Replace("\\{1}", "(?<hp>[-+0-9.,]+)") + "$";
    private static void StripBase(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        if (!TooltipText.IsMainThread || !Ready(world) || inSlot.Itemstack is not { } stack) return;
        var patterns = new[] { Pattern("When eaten: {0} sat", "{0}"), Pattern("liquid-when-drunk-saturation", "{0}"),
            Pattern("When eaten: {0} sat, {1} hp", "{0}", "{1}"), Pattern("liquid-when-drunk-saturation-hp", "{0}", "{1}") };
        var categories = Enum.GetValues<EnumFoodCategory>().Select(c => Lang.Get("Food Category: {0}", Lang.Get("foodcategory-" + c.ToString().ToLowerInvariant()))).ToHashSet();
        var lines = new List<string>();
        foreach (var line in dsc.ToString().Replace("\r", "").Split('\n'))
        {
            if (categories.Contains(line)) continue;
            var match = patterns.Select(p => Regex.Match(line, p)).FirstOrDefault(m => m.Success);
            if (match is null) lines.Add(line);
            else if (match.Groups["hp"].Success) lines.Add(Lang.Get("vsslssmod:food-native-health", match.Groups["hp"].Value));
        }
        dsc.Clear().Append(string.Join("\n", lines));
    }
    private static void StripMeal(IWorldAccessor world, ref string __result)
    {
        if (!TooltipText.IsMainThread || !Ready(world) || string.IsNullOrEmpty(__result)) return;
        // The verified method returns only a header, category satiety lines and an optional final health line.
        __result = string.Join("\n", __result.Replace("\r", "").Split('\n').Where(line =>
            line != Lang.Get("Nutrition Facts") && !Enum.GetValues<EnumFoodCategory>().Any(c =>
                Regex.IsMatch(line, Pattern("nutrition-facts-line-satiety", Lang.Get("foodcategory-" + c.ToString().ToLowerInvariant()), "{1}")
                    .Replace("(?<hp>[-+0-9.,]+)", "[-+0-9.,]+"))))).Trim();
        if (__result.Length > 0) __result += "\n";
    }
    public void Dispose() => harmony.UnpatchAll("vsslssmod.foodtooltips");
}
