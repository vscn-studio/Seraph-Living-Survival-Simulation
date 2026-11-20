using System.Globalization;
using System.Text;
using Seraph.Core.Framework;
using Seraph.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Seraph.Client;

/// <summary>Read-only projection of the server catalog using the same portion calculator as intake.</summary>
internal sealed class FoodInfoBehavior : CollectibleBehavior
{
    private readonly CollectibleObject collectible;

    public FoodInfoBehavior(CollectibleObject collectible) : base(collectible) => this.collectible = collectible;

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        // Creative-tab search and handbook caches can build descriptions on worker threads.
        // The game's TranslationService uses a non-concurrent dictionary, so our custom
        // localized nutrition text must only be composed on the client thread.
        if (!TooltipText.IsMainThread || world.Api is not ICoreClientAPI api || inSlot.Itemstack is not { } stack) return;
        try
        {
            var salt = stack.Attributes.GetDouble(World.SaltResidueBehavior.Key);
            if (salt > 0) dsc.AppendLine(LocalizedText.Get("vsslssmod:salt-residue", salt.ToString("0.##"), (int)(salt / 20)));
            AppendWaterInfo(api, world, dsc, stack);
            AppendFoodInfo(api, world, inSlot, dsc, stack);
        }
        catch (Exception ex)
        {
            // Tooltip generation is called by creative-search caches and by other mods.
            // A missing player or an evolving nutrient catalogue must never break that path.
            api.Logger.Debug("[vsslssmod] Food tooltip skipped for {0}: {1}", stack.Collectible?.Code, ex.Message);
        }
    }

    private static void AppendWaterInfo(ICoreClientAPI api, IWorldAccessor world, StringBuilder dsc, ItemStack stack)
    {
        ItemStack? liquid = World.WaterQuality.IsWater(stack) ? stack
            : stack.Block is BlockLiquidContainerBase container ? container.GetContent(stack) : null;
        if (!World.WaterQuality.IsWater(liquid)) return;

        var pos = api.World.Player?.Entity?.Pos?.AsBlockPos;
        var ambient = pos is null ? 15 : World.WaterQuality.Ambient(world, pos);
        var batch = World.WaterQuality.ReadLiquidAt(world, liquid, ambient, new());
        if (batch is null) return;
        var lines = World.WaterInfoText.Lines(batch,
            (key, args) => LocalizedText.Get("vsslssmod:" + key, args), includeWaste: false);
        TooltipText.AppendItem(dsc, string.Join("\n", lines));
    }

    private void AppendFoodInfo(ICoreClientAPI api, IWorldAccessor world, ItemSlot inSlot, StringBuilder dsc, ItemStack stack)
    {
        var bridge = api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge;
        var catalog = bridge?.Foods;
        var playerEntity = api.World.Player?.Entity;
        FoodDose? dose = null;
        var basis = "food-basis-item";
        if (stack.Block is BlockMeal meal)
        {
            var servings = meal.GetQuantityServings(world, stack);
            var contents = meal.GetNonEmptyContents(world, stack);
            if (!float.IsFinite(servings) || servings <= 0 || contents.Length == 0) return;
            basis = "food-basis-meal";
            if (catalog is not null)
            {
                dose = new();
                dose.Add(catalog.MealComposition(world, stack, contents, world.Api.GetCookingRecipe(stack.Attributes.GetString("recipeCode")) is null), Math.Min(1, servings));
            }
        }
        else if (stack.Block is BlockLiquidContainerBase container)
        {
            var liquid = container.GetContent(stack);
            if (liquid is null || World.WaterQuality.IsWater(liquid)) return;
            var props = BlockLiquidContainerBase.GetContainableProps(liquid);
            if (props is null || !float.IsFinite(props.ItemsPerLitre) || props.ItemsPerLitre <= 0
                || liquid.Collectible.GetNutritionProperties(world, liquid, playerEntity) is null && catalog?.Find(liquid) is null) return;
            basis = "food-basis-sip";
            dose = catalog?.Composition(liquid, Math.Min(liquid.StackSize / (double)props.ItemsPerLitre,
                Math.Max(1 / props.ItemsPerLitre, container.DrinkPortionSize)), true, world, playerEntity);
        }
        else
        {
            if (World.WaterQuality.IsWater(stack) || catalog?.Find(stack) is null
                && collectible.GetNutritionProperties(world, stack, playerEntity) is null) return;
            dose = catalog?.Composition(stack, 1, collectible.MatterState == EnumMatterState.Liquid, world, playerEntity);
            if (collectible.MatterState == EnumMatterState.Liquid) basis = "food-basis-litre";
        }
        if (catalog is null || dose is null || !dose.Mapped) return;
        var nutrientValues = dose.Nutrients;
        var nutrientKnown = dose.Known;
        var nutrientDefinitions = Nutrients.All;
        if (nutrientValues is null || nutrientKnown is null || nutrientDefinitions.Length == 0) return;
        // Compact grouped lines; never emit leading/trailing blank paragraphs.
        var title = L("food-title");
        if (dsc.ToString().Contains(title, StringComparison.Ordinal)) return;
        var section = new StringBuilder();
        section.Append(title).Append(" · ").AppendLine(L(basis));
        if (double.IsFinite(dose.EnergyKJ) && Math.Round(dose.EnergyKJ, 2) > 0)
            section.AppendLine(L("food-energy-only", N(dose.EnergyKJ / 4.184)));
        if (double.IsFinite(dose.WaterL) && Math.Round(dose.WaterL * 1000, 2) > 0)
            section.AppendLine(L("food-water-only", N(dose.WaterL * 1000)));
        // Resolve by stable nutrient IDs.  The catalogue is intentionally allowed to
        // evolve (for example, removed B vitamins must not turn a tooltip into a
        // client crash when an external mod opens its creative-tab search cache).
        AppendGroup("food-group-macros", "carbohydrate", "protein", "fat");
        AppendGroup("food-group-minerals", "electrolytes", "calcium", "iron", "zinc");
        AppendGroup("food-group-vitamins", "a", "c", "bcomplex");
        TooltipText.AppendItem(dsc, section.ToString());

        void AppendGroup(string group, params string[] ids)
        {
            var entries = ids.Select(id => {
                var sources = id == "ironzinc" ? new[] { "iron", "zinc" } : new[] { id };
                var indices = sources.Select(source => Array.FindIndex(nutrientDefinitions, n => n.Id == source))
                    .Where(i => i >= 0 && i < nutrientKnown.Length && i < nutrientValues.Length
                        && nutrientKnown[i] && double.IsFinite(nutrientValues[i])).ToArray();
                var value = id == "ironzinc" ? indices.Select(i => nutrientValues[i]).DefaultIfEmpty().Min() : indices.Sum(i => nutrientValues[i]);
                var definition = indices.Length > 0 ? nutrientDefinitions[indices[0]] : null;
                return (Id: id, Value: value, Definition: definition);
            }).Where(e => e.Definition is not null && Math.Round(e.Value, 2) > 0).ToArray();
            if (entries.Length == 0) return;
            // Explicit short lines avoid an over-wide unbreakable rich-text line.
            var column = 0;
            foreach (var entry in entries)
            {
                if (column == 0) section.Append(L(group)).Append(": ");
                else if (column % 2 == 0) section.Append('\n');
                else section.Append(" · ");
                section.Append("<font color=\"").Append(entry.Definition!.Color).Append("\">")
                    .Append(L("nutrient-short-" + entry.Id)).Append('(')
                    .Append(N(entry.Value) + " " + entry.Definition.Unit)
                    .Append(")</font>");
                column++;
            }
            section.Append('\n');
        }
    }
    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string L(string key, params object[] args) => LocalizedText.Get("vsslssmod:" + key, args);
}
