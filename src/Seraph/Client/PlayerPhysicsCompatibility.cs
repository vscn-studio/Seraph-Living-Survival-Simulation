using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seraph.Client;

/// <summary>
/// Walkable Entities runs an additional player-physics patch. During a
/// gamemode transition the vanilla behavior can render once before its
/// player/capi state is rebuilt; the third-party patch then throws an NRE.
/// Keep that invalid frame from terminating the client.
/// </summary>
internal sealed class PlayerPhysicsCompatibility : IDisposable
{
    private const string HarmonyId = "vsslssmod.walkable-physics-compat";
    private static PlayerPhysicsCompatibility? active;
    private readonly ICoreClientAPI api;
    private readonly Harmony harmony = new(HarmonyId);
    private bool patched;
    private bool disposed;
    private long lastWarningMs = long.MinValue;

    internal PlayerPhysicsCompatibility(ICoreClientAPI api)
    {
        this.api = api;
        if (!HasWalkableEntities(api)) return;

        var physicsType = AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorPlayerPhysics");
        var simPhysics = physicsType is null ? null
            : AccessTools.Method(physicsType, "SimPhysics", [typeof(float), typeof(EntityPos)]);
        var renderFrame = physicsType is null ? null
            : AccessTools.Method(physicsType, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]);
        if (simPhysics is null && renderFrame is null)
        {
            api.Logger.Warning("[vsslssmod] Walkable Entities physics compatibility target unavailable.");
            return;
        }

        active = this;
        if (simPhysics is not null)
            harmony.Patch(simPhysics, finalizer: new HarmonyMethod(typeof(PlayerPhysicsCompatibility), nameof(SimPhysicsFinalizer)));
        if (renderFrame is not null)
            harmony.Patch(renderFrame, finalizer: new HarmonyMethod(typeof(PlayerPhysicsCompatibility), nameof(RenderFrameFinalizer)));
        patched = true;
    }

    private static bool HasWalkableEntities(ICoreClientAPI api)
    {
        try
        {
            return api.ModLoader.Mods.Any(mod =>
                string.Equals(mod.Info.ModID, "walkableentities", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static Exception? SimPhysicsFinalizer(Exception? __exception)
        => active?.SuppressTransitionNullReference(__exception) ?? __exception;

    private static Exception? RenderFrameFinalizer(Exception? __exception)
        => active?.SuppressTransitionNullReference(__exception) ?? __exception;

    private Exception? SuppressTransitionNullReference(Exception? exception)
    {
        if (exception is not NullReferenceException) return exception;
        var now = api.World?.ElapsedMilliseconds ?? Environment.TickCount64;
        if (now - lastWarningMs >= 1000)
        {
            lastWarningMs = now;
            api.Logger.Warning("[vsslssmod] Ignored a transient player-physics null reference during a gamemode transition.");
        }
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ReferenceEquals(active, this)) active = null;
        if (patched) harmony.UnpatchAll(HarmonyId);
    }
}
