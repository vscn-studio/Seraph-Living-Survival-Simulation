using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;

namespace Seraph.Client;

/// <summary>1.22.7: limit the fix to native character pages and scrollbars with no overflow.</summary>
internal sealed class NativeCharacterScrollbars : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.character-scrollbars");
    private static readonly ConditionalWeakTable<GuiElementScrollbar, object> Candidates = new();
    private static readonly FieldInfo? Elements = AccessTools.Field(typeof(GuiComposer), "interactiveElements");
    private static readonly FieldInfo? Visible = AccessTools.Field(typeof(GuiElementScrollbar), "visibleHeight");
    private static readonly FieldInfo? Total = AccessTools.Field(typeof(GuiElementScrollbar), "totalHeight");

    internal NativeCharacterScrollbars()
    {
        foreach (var type in new[] { typeof(GuiElementScrollbar), typeof(GuiElementCompactScrollbar) })
        foreach (var method in new[] { "ComposeElements", "RenderInteractiveElements", "OnMouseDownOnElement", "OnMouseMove", "OnMouseUp", "OnMouseWheel", "OnKeyDown" })
            if (AccessTools.DeclaredMethod(type, method) is { } target)
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(NativeCharacterScrollbars), nameof(HasOverflow)));
    }

    internal void HideUnused(GuiComposer composer)
    {
        if (Elements?.GetValue(composer) is not Dictionary<string, GuiElement> elements) return;
        foreach (var bar in elements.Values.OfType<GuiElementScrollbar>())
            Candidates.GetValue(bar, static _ => new object());
    }

    private static bool HasOverflow(GuiElementScrollbar __instance)
    {
        if (!Candidates.TryGetValue(__instance, out _) || Visible?.GetValue(__instance) is not float visible
            || Total?.GetValue(__instance) is not float total || visible <= 0) return true;
        return total > visible + .5f;
    }

    public void Dispose() => harmony.UnpatchAll("vsslssmod.character-scrollbars");
}
