using HarmonyLib;
using System.Reflection.Emit;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Keep the native health/hunger behavior types, armor delegates, DoT, fall damage and death events.
/// Their health getters/setters become a projection of the independent regional ledger.</summary>
internal sealed class NativePhysiologyBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.physiology");
    internal sealed class DamageContext(Entity entity, DamageSource source, HitRegion region, float raw, DamageContext? previous)
    {
        internal readonly Entity Entity = entity;
        internal readonly DamageSource Source = source;
        internal readonly HitRegion Region = region;
        internal readonly DamageContext? Previous = previous;
        internal readonly float Raw = raw;
        internal float AfterShield = raw, AfterArmor = raw, AfterFat = raw;
        internal bool FatApplied;
        internal string ArmorSlot = "none";
    }
    [ThreadStatic] private static DamageContext? context;
    internal static HitRegion? CurrentRegion(Entity entity, DamageSource? source) => context is { } c
        && ReferenceEquals(c.Entity, entity) && ReferenceEquals(c.Source, source) ? c.Region : null;
    private static readonly System.Reflection.MethodInfo Detox = AccessTools.DeclaredMethod(typeof(EntityBehaviorHunger), "detox");

    internal NativePhysiologyBridge() : this(null) { }
    internal NativePhysiologyBridge(ICoreAPI? api)
    {
        var health = typeof(EntityBehaviorHealth);
        Patch(AccessTools.PropertyGetter(health, "Health"), nameof(GetHealth));
        Patch(AccessTools.PropertySetter(health, "Health"), nameof(SetHealth), nameof(AfterSetHealth));
        Patch(AccessTools.PropertyGetter(health, "MaxHealth"), nameof(GetMaximum));
        Patch(AccessTools.Method(health, "UpdateMaxHealth"), nameof(UpdateMaximum));
        Patch(AccessTools.Method(health, "ApplyRegenAndHunger"), nameof(NativeRegen));
        harmony.Patch(AccessTools.Method(health, "OnEntityReceiveDamage"),
            prefix: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(BeginDamage)),
            finalizer: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(EndDamage)));
        harmony.Patch(AccessTools.Method(health, "ApplyOnDamageDelegates"),
            postfix: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(AfterDelegates)));
        var wearable = typeof(ModSystemWearableStats);
        try
        {
            harmony.Patch(AccessTools.Method(wearable, "handleDamaged"),
                postfix: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(AfterArmor)),
                transpiler: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(ArmorRegion)));
            harmony.Patch(AccessTools.Method(wearable, "applyShieldProtection"),
                postfix: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(AfterShield)));
        }
        catch (Exception ex) { api?.Logger.Warning("[vsslssmod] Native armor region hook unavailable: {0}", ex.Message); }
        foreach (var method in new[] { "OnEntityReceiveSaturation", "UpdateNutrientHealthBoost", "ReduceSaturation", "OnEntityReceiveDamage" })
            Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorHunger), method), nameof(NativeNutrition));
        Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorHunger), "SlowTick"), nameof(HungerSlowTick));
        Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorHunger), "OnGameTick"), nameof(HungerTick));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorBodyTemperature), "OnGameTick"),
            postfix: new HarmonyMethod(typeof(NativePhysiologyBridge), nameof(AfterTemperatureTick)));
    }
    private void Patch(System.Reflection.MethodInfo method, string prefix, string? postfix = null) => harmony.Patch(method,
        prefix: new HarmonyMethod(typeof(NativePhysiologyBridge), prefix),
        postfix: postfix is null ? null : new HarmonyMethod(typeof(NativePhysiologyBridge), postfix));
    private static ServerCoordinator? Owner(Entity entity) => entity.World?.Side == EnumAppSide.Server
        ? entity.Api.ModLoader.GetModSystem<SeraphModSystem>().Server : null;
    private static bool Owned(EntityBehavior b, out ServerCoordinator owner)
    { owner = Owner(b.entity)!; return owner?.OwnsPhysiology(b.entity) == true; }
    private static bool GetHealth(EntityBehaviorHealth __instance, ref float __result)
    {
        if (!Owned(__instance, out var owner)) return true;
        __result = owner.OwnHealth(__instance.entity); return false;
    }
    private static bool GetMaximum(EntityBehaviorHealth __instance, ref float __result)
    { if (!Owned(__instance, out var owner)) return true; __result = owner.OwnMaxHealth(__instance.entity); return false; }
    private static void SetHealth(EntityBehaviorHealth __instance, ref float value)
    {
        if (!Owned(__instance, out var owner)) return;
        var c = context;
        if (c is not null && ReferenceEquals(c.Entity, __instance.entity) && value < owner.OwnHealth(__instance.entity) && !c.FatApplied)
        {
            var damage = owner.OwnHealth(__instance.entity) - value;
            ApplyFat(owner, c, ref damage);
            value = owner.OwnHealth(__instance.entity) - damage;
        }
        value = owner.ChangeOwnHealth(__instance.entity, value, c?.Source);
    }
    private static void AfterSetHealth(EntityBehaviorHealth __instance)
    {
        if (context is null && Owned(__instance, out var owner)) owner.CheckOwnDeath(__instance.entity);
    }
    private static bool UpdateMaximum(EntityBehaviorHealth __instance)
    { if (!Owned(__instance, out var owner)) return true; owner.ProjectPhysiology(__instance.entity); return false; }
    private static bool NativeRegen(EntityBehaviorHealth __instance) => !Owned(__instance, out _);
    private static bool NativeNutrition(EntityBehaviorHunger __instance) => !Owned(__instance, out _);
    private static void AfterTemperatureTick(EntityBehaviorBodyTemperature __instance)
        => Owner(__instance.entity)?.ProjectColdVisuals(__instance.entity);
    private static bool HungerSlowTick(EntityBehaviorHunger __instance)
    {
        if (!Owned(__instance, out _)) return true;
        // This separate 6-second listener otherwise inflicts native starvation even with OnGameTick suppressed.
        NativeStatInputs.SetOwned(__instance.entity, "hungerrate", "resistcold", null);
        return false;
    }
    private static bool HungerTick(EntityBehaviorHunger __instance, float deltaTime)
    {
        if (!Owned(__instance, out var owner)) return true;
        // Alcohol/psychedelic decay remains the original implementation (and external patches).
        Detox.Invoke(__instance, [deltaTime]);
        owner.ProjectPhysiology(__instance.entity); return false;
    }
    private static void BeginDamage(EntityBehaviorHealth __instance, DamageSource damageSource, ref float damage, out DamageContext? __state)
    {
        __state = context;
        if (Owned(__instance, out var combatOwner))
        {
            context = new(__instance.entity, damageSource, damage > 0
                ? ServerCoordinator.ResolveHitRegion(__instance.entity, damageSource) : new("general", InjuryEvidence.Unlocated), damage, context);
            if (damage > 0) combatOwner.ApplyCombatProtection(__instance.entity, damageSource, ref damage);
        }
        else context = null;
        if (damageSource.Type == EnumDamageType.Heal && NativeBandageBridge.SuppressesHealing(__instance.entity)) damage = 0;
        if (damageSource.Source == EnumDamageSource.Weather && damageSource.Type == EnumDamageType.Frost
            && Owner(__instance.entity)?.OwnsThermal(__instance.entity) == true) damage = 0;
        if (damageSource.Source == EnumDamageSource.Revive && Owned(__instance, out var owner)) owner.ConfirmEngineRevival(__instance.entity);
    }
    private static void ApplyFat(ServerCoordinator owner, DamageContext c, ref float damage)
    {
        c.FatApplied = true;
        damage = owner.ApplyFatProtection(c.Entity, c.Source, damage);
        c.AfterFat = damage;
    }
    private static void AfterDelegates(EntityBehaviorHealth __instance, DamageSource damageSource, ref float damage)
    {
        if (context is { } c && ReferenceEquals(c.Entity, __instance.entity) && ReferenceEquals(c.Source, damageSource)
            && !c.FatApplied && Owned(__instance, out var owner)) ApplyFat(owner, c, ref damage);
    }
    private static void AfterShield(float __result) { if (context is { } c) c.AfterShield = __result; }
    private static void AfterArmor(float __result) { if (context is { } c) c.AfterArmor = __result; }
    private static IEnumerable<CodeInstruction> ArmorRegion(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var roll = AccessTools.Method(typeof(Random), nameof(Random.NextDouble), Type.EmptyTypes);
        if (code.Count(i => i.Calls(roll)) != 1) throw new InvalidOperationException("Unsupported native armor selector; expected one random roll.");
        foreach (var i in code)
        {
            if (!i.Calls(roll)) { yield return i; continue; }
            // Keep all native protection, shield, clothing and durability operations.
            var load = new CodeInstruction(OpCodes.Ldarg_1); load.labels.AddRange(i.labels); load.blocks.AddRange(i.blocks);
            yield return load;
            yield return new(OpCodes.Ldarg_3);
            yield return CodeInstruction.Call(typeof(NativePhysiologyBridge), nameof(ArmorRoll));
        }
    }
    private static double ArmorRoll(Random random, IPlayer player, DamageSource source)
    {
        if (context is not { } c || !ReferenceEquals(c.Entity, player.Entity) || !ReferenceEquals(c.Source, source)
            || c.Region.VitalIndex < 0) return random.NextDouble();
        c.ArmorSlot = c.Region.VitalIndex switch { 0 => "head", 4 or 5 => "legs", _ => "body" };
        return c.Region.VitalIndex switch { 0 => .1, 4 or 5 => .85, _ => .4 };
    }
    private static Exception? EndDamage(Exception? __exception, DamageContext? __state)
    {
        if (context is { } c && Owner(c.Entity) is { } owner)
            owner.RecordCombatDiagnostic(c.Entity, c.Raw, c.AfterShield, c.AfterArmor, c.AfterFat, c.ArmorSlot);
        context = __state; return __exception;
    }
    public void Dispose() => harmony.UnpatchAll("vsslssmod.physiology");
}
