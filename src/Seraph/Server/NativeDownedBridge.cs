using System.Reflection;
using HarmonyLib;
using Seraph.Application;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Seraph.Server;

/// <summary>Version-checked lifecycle adapter. Downed retains countdowns, choices, rescue and executions.</summary>
internal sealed class NativeDownedBridge : IDownedDelegate, IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.downed");
    private readonly ICoreServerAPI api;
    private static Type? behaviorType;
    private static MethodInfo? enter, revive, cooldown;
    private static FieldInfo? damageSource;
    [ThreadStatic] private static bool consumedHealing;
    public string ProviderId => "downed";
    public bool OwnsRevival => enter is not null;

    internal NativeDownedBridge(ICoreServerAPI api)
    {
        this.api = api;
        behaviorType = AccessTools.TypeByName("Downed.EntityBehaviorDowned");
        if (behaviorType is null) return;
        enter = AccessTools.DeclaredMethod(behaviorType, "EnterDowned", [typeof(Entity), typeof(string)]);
        revive = AccessTools.DeclaredMethod(behaviorType, "ReviveByHealing", [typeof(Entity)]);
        cooldown = AccessTools.DeclaredMethod(behaviorType, "IsOnDownedCooldown", Type.EmptyTypes);
        damageSource = AccessTools.Field(behaviorType, "lastDamageSource");
        var onDamage = AccessTools.DeclaredMethod(behaviorType, "OnDamaged", [typeof(float), typeof(DamageSource)]);
        var exit = AccessTools.DeclaredMethod(behaviorType, "ExitDowned", [typeof(bool), typeof(Entity), typeof(string)]);
        var forceDown = AccessTools.DeclaredMethod(behaviorType, "ForceDown", [typeof(Entity), typeof(string)]);
        if (enter is null || revive is null || cooldown is null || damageSource is null || onDamage is null || exit is null
            || forceDown is null || AccessTools.PropertyGetter(behaviorType, "config") is null)
        {
            enter = null;
            api.Logger.Error("[vsslssmod] Unsupported Downed API; native death fallback enabled.");
            return;
        }
        harmony.Patch(onDamage, prefix: new(typeof(NativeDownedBridge), nameof(Damage)));
        harmony.Patch(revive, prefix: new(typeof(NativeDownedBridge), nameof(Healing)));
        harmony.Patch(exit, prefix: new(typeof(NativeDownedBridge), nameof(Exiting)),
            postfix: new(typeof(NativeDownedBridge), nameof(Exited)));
        harmony.Patch(forceDown,
            prefix: new(typeof(NativeDownedBridge), nameof(ForceDown)));
    }
    private static EntityBehavior? Behavior(Entity entity) => behaviorType is null ? null
        : entity.GetBehavior("playerdowned") is { } b && behaviorType.IsInstanceOfType(b) ? b : null;
    private static ServerCoordinator? Owner(Entity entity) => entity.World?.Side == EnumAppSide.Server
        ? entity.Api.ModLoader.GetModSystem<SeraphModSystem>().Server : null;
    internal static bool IsActive(Entity entity) => enter is not null && Behavior(entity) is not null
        && entity.WatchedAttributes.GetBool("downed");
    internal static float Floor(Entity entity)
    {
        var b = Behavior(entity);
        var config = b is null ? null : AccessTools.PropertyGetter(behaviorType, "config")?.Invoke(b, null);
        var raw = config is null ? null : AccessTools.Field(config.GetType(), "DownedFloorHealth")?.GetValue(config)
            ?? AccessTools.Property(config.GetType(), "DownedFloorHealth")?.GetValue(config);
        return raw is float floor && float.IsFinite(floor) ? Math.Clamp(floor, .001f, 100) : .1f;
    }
    internal static bool TryEnter(Entity entity, DamageSource source, bool force = false)
    {
        if (enter is null || Behavior(entity) is not { } b) return false;
        if (IsActive(entity)) return true;
        try
        {
            if (!force && cooldown!.Invoke(b, null) is true) return false;
            damageSource!.SetValue(b, source);
            enter.Invoke(b, [source.CauseEntity ?? source.SourceEntity, null]);
            return IsActive(entity);
        }
        catch (Exception ex)
        {
            entity.Api.Logger.Error("[vsslssmod] Downed entry failed; native death fallback: {0}", ex);
            // A partial external call must not leave a false invulnerable state.
            entity.WatchedAttributes.SetBool("downed", false);
            return false;
        }
    }
    private static bool Damage(EntityBehavior __instance, float damage, DamageSource dmgSource, ref float __result)
    {
        if (Owner(__instance.entity)?.OwnsPhysiology(__instance.entity) != true) return true;
        // Preserve upstream armor delegates. The regional writer below owns the lethal threshold.
        __result = IsActive(__instance.entity) && dmgSource.Type != EnumDamageType.Heal ? 0 : damage;
        return false;
    }
    private static bool Healing(EntityBehavior __instance, ref bool __result)
    {
        if (Owner(__instance.entity)?.OwnsPhysiology(__instance.entity) != true || consumedHealing) return true;
        __result = false; return false; // Target lookup is not a completed treatment.
    }
    internal static bool RescueAfterConsumedTreatment(Entity target, Entity? healer)
    {
        if (!IsActive(target) || revive is null || Behavior(target) is not { } b) return false;
        var previous = consumedHealing;
        try { consumedHealing = true; return revive.Invoke(b, [healer]) is true; }
        finally { consumedHealing = previous; }
    }
    private static void Exiting(EntityBehavior __instance, bool die, out bool __state)
        => __state = !die && IsActive(__instance.entity);
    private static void Exited(EntityBehavior __instance, bool __state)
    {
        if (__state) Owner(__instance.entity)?.CompleteDownedRescue(__instance.entity);
    }
    private static bool ForceDown(EntityBehavior __instance, Entity? byEntity, ref bool __result)
    {
        if (Owner(__instance.entity) is not { } owner || !owner.OwnsPhysiology(__instance.entity)) return true;
        __result = !IsActive(__instance.entity) && TryEnter(__instance.entity, new DamageSource {
            Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury, CauseEntity = byEntity }, force: true);
        owner.SyncDowned(__instance.entity); return false;
    }
    public bool IsDowned(string playerUid) => api.World.PlayerByUid(playerUid)?.Entity is { } e && IsActive(e);
    public bool TryEnter(string playerUid) => api.World.PlayerByUid(playerUid)?.Entity is { } e
        && TryEnter(e, new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury });
    public bool TryRescue(string playerUid, string? reviverUid) => api.World.PlayerByUid(playerUid)?.Entity is { } e
        && RescueAfterConsumedTreatment(e, reviverUid is null ? null : api.World.PlayerByUid(reviverUid)?.Entity);
    public void Dispose() { harmony.UnpatchAll("vsslssmod.downed"); enter = null; revive = null; cooldown = null; damageSource = null; behaviorType = null; }
}
