using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Seraph.Server;

/// <summary>Diagnostic metadata only. Call on the server main thread; never feeds physical inputs.</summary>
internal static class VanillaEnvironmentProbe
{
    private const int MaxSlots = 32;
    private const int MaxBehaviors = 64;
    private const int MaxItemLines = 6;
    private const int MaxLines = 8;
    private const int MaxLineLength = 240;

    public static string[] Capture(IServerPlayer player, double observedAtServerSeconds)
    {
        // Resolve once so read failures can still be reported without reaccessing the player property.
        var languageCode = "en";
        try
        {
            var requestedLanguage = player.LanguageCode;
            if (!string.IsNullOrWhiteSpace(requestedLanguage)) languageCode = requestedLanguage;
        }
        catch (Exception) { }

        // Keep the two optional observations independent, including their failure paths.
        var lines = ReadClothing(player, languageCode);
        return lines.Take(MaxLines - 1)
            .Append(ReadCachedHeat(player, observedAtServerSeconds, languageCode))
            .Select(line => Clip(line, MaxLineLength)).ToArray();
    }

    private static List<string> ReadClothing(IPlayer player, string languageCode)
    {
        var lines = new List<string>(MaxItemLines + 2);
        try
        {
            var inventory = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (inventory is null)
            {
                lines.Add(Local(languageCode, "probe-clothing-inventory-unavailable"));
                return lines;
            }

            var count = inventory.Count;
            if (count < 0) throw new InvalidDataException("Negative inventory slot count.");
            var known = 0;
            var unknown = 0;
            var errors = 0;
            var armor = 0;
            var details = 0;
            double subtotal = 0;
            for (var i = 0; i < Math.Clamp(count, 0, MaxSlots); i++)
            {
                try
                {
                    var slot = inventory[i];
                    var stack = slot?.Itemstack;
                    if (stack is null) continue;
                    if (slot is ItemSlotCharacter { Type: EnumCharacterDressType.ArmorHead or EnumCharacterDressType.ArmorBody or EnumCharacterDressType.ArmorLegs })
                    {
                        armor++;
                        continue;
                    }

                    var row = InspectClothing(slot!, stack, languageCode);
                    if (row.EffectiveWarmth is { } warmth) { known++; subtotal += warmth; }
                    else unknown++;
                    if (details++ < MaxItemLines) lines.Add(Clip(row.Line, MaxLineLength));
                }
                catch (Exception ex)
                {
                    errors++;
                    if (details++ < MaxItemLines) lines.Add(Local(languageCode, "probe-clothing-slot-failed", i, Clip(ex.GetType().Name, 40)));
                }
            }

            lines.Insert(0, Clip(Local(languageCode, "probe-clothing-summary", known, unknown, errors, armor,
                Math.Max(0, count - MaxSlots), Math.Max(0, details - MaxItemLines), Number(subtotal)), MaxLineLength));
        }
        catch (Exception ex)
        {
            lines.Add(Local(languageCode, "probe-clothing-unavailable", Clip(ex.GetType().Name, 40)));
        }
        return lines;
    }

    private static (string Line, double? EffectiveWarmth) InspectClothing(ItemSlot slot, ItemStack stack, string languageCode)
    {
        var collectible = stack.Collectible;
        var code = collectible?.Code;
        var label = Clip(Clip(code?.Domain, 20) + ":" + Clip(code?.Path, 60), 64);
        var dress = DressName(languageCode, slot is ItemSlotCharacter character ? character.Type : EnumCharacterDressType.Unknown);
        var attributes = stack.ItemAttributes;
        var rawWarmth = attributes?["warmth"];
        // The native getter defaults absent warmth to zero, but never default a missing condition to 1.
        var maxWarmth = rawWarmth?.Exists == true ? rawWarmth.AsFloat(float.NaN) : 0;
        var conditionAttribute = stack.Attributes?["condition"];
        var condition = conditionAttribute is FloatAttribute value && float.IsFinite(value.value) && value.value is >= 0 and <= 1
            ? (float?)value.value : null;
        var conditionText = condition is { } c ? Number(c) : Local(languageCode,
            conditionAttribute is null ? "probe-value-unknown-missing" : "probe-value-unknown-invalid");
        var warmthText = rawWarmth?.Exists != true ? Local(languageCode, "probe-value-missing")
            : float.IsFinite(maxWarmth) && maxWarmth >= 0 ? Number(maxWarmth) : Local(languageCode, "probe-value-unknown-invalid");

        // Do not invoke GetCollectibleInterface, IsArmorType, GetDressType or any external wearable method.
        // Exact types exclude subclasses and custom collectible dispatch. This is a metadata rule estimate,
        // not a read of vanilla's private/cached clothingBonus.
        if (collectible is null || !UsesNativeWearable(collectible))
            return (Local(languageCode, "probe-clothing-unknown-adapter", dress, label, warmthText, conditionText), null);
        var category = attributes?["clothescategory"].AsString();
        if (slot is not ItemSlotCharacter characterSlot || !Enum.TryParse<EnumCharacterDressType>(category, true, out var categoryType)
            || !Enum.IsDefined(categoryType) || categoryType == EnumCharacterDressType.Unknown || categoryType != characterSlot.Type)
            return (Local(languageCode, "probe-clothing-unknown-category", dress, label, warmthText, conditionText), null);
        if (condition is null || !float.IsFinite(maxWarmth) || maxWarmth < 0)
            return (Local(languageCode, "probe-clothing-unknown-metadata", dress, label, warmthText, conditionText), null);

        var effective = Math.Min((double)maxWarmth, 2d * condition.Value * maxWarmth);
        return (Local(languageCode, "probe-clothing-native-rule", dress, label, warmthText, conditionText, Number(effective)), effective);
    }

    private static bool UsesNativeWearable(CollectibleObject collectible)
    {
        if (collectible.GetType() != typeof(Item)) return false;
        var behaviors = collectible.CollectibleBehaviors;
        if (behaviors is null || behaviors.Length > MaxBehaviors) return false;
        CollectibleBehavior? wearable = null;
        CollectibleBehavior? supplier = null;
        foreach (var behavior in behaviors)
        {
            if (wearable is null && behavior is IWearable) wearable = behavior;
            if (supplier is null && behavior is IWearableStatsSupplier) supplier = behavior;
        }
        if (wearable is null || !ReferenceEquals(wearable, supplier)) return false;
        var type = wearable.GetType();
        return type.FullName == "Vintagestory.GameContent.CollectibleBehaviorWearable"
            && type.Assembly.GetName().Name == "VSSurvivalMod";
    }

    private static string ReadCachedHeat(IPlayer player, double observedAtServerSeconds, string languageCode)
    {
        var readTime = double.IsFinite(observedAtServerSeconds)
            ? Local(languageCode, "probe-time-seconds", Number(observedAtServerSeconds)) : Local(languageCode, "probe-value-unknown");
        try
        {
            var tree = player.Entity?.WatchedAttributes.GetTreeAttribute("bodyTemp");
            if (tree is null) return Local(languageCode, "probe-heat-tree-unavailable", readTime);
            var attribute = tree["nearHeatSourceStrength"];
            if (attribute is null) return Local(languageCode, "probe-heat-value-missing", readTime);
            if (attribute is not FloatAttribute value) return Local(languageCode, "probe-heat-invalid-type", readTime);
            if (!float.IsFinite(value.value)) return Local(languageCode, "probe-heat-nonfinite", readTime);
            return Local(languageCode, "probe-heat-cached", Number(value.value), readTime);
        }
        catch (Exception ex)
        {
            return Local(languageCode, "probe-heat-read-failed", Clip(ex.GetType().Name, 40), readTime);
        }
    }

    private static string DressName(string languageCode, EnumCharacterDressType dress) => Local(languageCode, dress switch
    {
        EnumCharacterDressType.Head => "probe-slot-head",
        EnumCharacterDressType.Shoulder => "probe-slot-shoulder",
        EnumCharacterDressType.UpperBody => "probe-slot-upper-body",
        EnumCharacterDressType.UpperBodyOver => "probe-slot-outerwear",
        EnumCharacterDressType.LowerBody => "probe-slot-lower-body",
        EnumCharacterDressType.Foot => "probe-slot-feet",
        EnumCharacterDressType.Hand => "probe-slot-hands",
        EnumCharacterDressType.Neck => "probe-slot-neck",
        EnumCharacterDressType.Emblem => "probe-slot-emblem",
        EnumCharacterDressType.Face => "probe-slot-face",
        EnumCharacterDressType.Waist => "probe-slot-waist",
        EnumCharacterDressType.Arm => "probe-slot-arms",
        EnumCharacterDressType.ArmorHead => "probe-slot-armor-head",
        EnumCharacterDressType.ArmorBody => "probe-slot-armor-body",
        EnumCharacterDressType.ArmorLegs => "probe-slot-armor-legs",
        _ => "probe-slot-unknown"
    });

    private static string Local(string languageCode, string key, params object[] args) =>
        LocalizedText.GetL(languageCode, "vsslssmod:" + key, args);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Clip(string? text, int limit)
    {
        if (text is null) return "?";
        var length = Math.Min(text.Length, limit);
        var chars = text.AsSpan(0, length).ToArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i])) chars[i] = '?';
        if (text.Length > limit && length >= 3)
            chars.AsSpan(length - 3).Fill('.');
        return new string(chars);
    }
}
