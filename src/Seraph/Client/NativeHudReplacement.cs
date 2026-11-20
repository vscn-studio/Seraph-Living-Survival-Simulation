using HarmonyLib;
using Vintagestory.API.Client;

namespace Seraph.Client;

/// <summary>Suppress native HUD rendering only while our own complete, fresh projection is visible.</summary>
internal sealed class NativeHudReplacement : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.hud");
    internal static bool Active { get; set; }
    internal static bool Available { get; private set; }
    public NativeHudReplacement(ICoreClientAPI api)
    {
        Active = false;
        Available = false;
        var type = AccessTools.TypeByName("Vintagestory.Client.NoObf.HudStatbar");
        var method = type is null ? null : AccessTools.DeclaredMethod(type, "OnRenderGUI", [typeof(float)]);
        if (method is null) { api.Logger.Warning("[vsslssmod] Native HUD target unavailable; native bars retained."); return; }
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeHudReplacement), nameof(RenderNative)));
        Available = true;
    }
    private static bool RenderNative() => !Active;
    public void Dispose() { Active = false; Available = false; harmony.UnpatchAll("vsslssmod.hud"); }
}
