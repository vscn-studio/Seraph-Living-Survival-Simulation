using HarmonyLib;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Native 1.22.7 execution points. Client prediction uses only a fresh self snapshot.
/// Server gates remain authoritative; no fall-damage, inventory-drop or oxygen writer.</summary>
internal sealed class NativeInjuryActionBridge : IDisposable
{
    private readonly ICoreAPI? api;
    private readonly Harmony attackTiming = new("vsslssmod.adrenaline-attacks");
    private static bool oxygenHookMatched;
    internal static bool OxygenReplacementReady { get; private set; }
    private readonly Harmony harmony = new("vsslssmod.injury-actions");
    private sealed record GroundState(bool Jump, long LastJump, double Capacity);
    private sealed class HitCycle { internal bool Charged; }
    private static readonly ConditionalWeakTable<PlayerAnimationManager, HitCycle> HitCycles = new();
    internal NativeInjuryActionBridge() : this(null) { }
    internal NativeInjuryActionBridge(ICoreAPI? api)
    {
        this.api = api;
        attackTiming.Patch(AccessTools.Method(typeof(RunningAnimation), nameof(RunningAnimation.Progress)),
            prefix: new HarmonyMethod(typeof(AdrenalineAttackTiming), nameof(AdrenalineAttackTiming.Progress)));
        Patch(typeof(PModuleOnGround), "DoApply", nameof(GroundStart), finalizer: nameof(GroundEnd));
        Patch(typeof(EntityAgent), "GetWalkSpeedMultiplier", postfix: nameof(SprintSpeed));
        Patch(typeof(EntityAgent), "OnInteract", nameof(AttackStart));
        Patch(typeof(PlayerAnimationManager), "StartHeldHitAnim", prefix: nameof(AttackAnimation), postfix: nameof(AttackCycle));
        Patch(typeof(PlayerAnimationManager), "ResetAnimation", postfix: nameof(ResetHitCycle));
        Patch(typeof(PlayerAnimationManager), "StopHeldAttackAnim", postfix: nameof(StopHitCycle));
        oxygenHookMatched = false;
        OxygenReplacementReady = Patch(typeof(EntityBehaviorBreathe), "OnGameTick", transpiler: nameof(OxygenRecovery)) && oxygenHookMatched;
        Patch(typeof(Block), "OnGettingBroken", nameof(MiningStart), postfix: nameof(MiningEnd));
        Patch(typeof(ItemBow), "OnHeldInteractStep", nameof(BowTime));
        Patch(typeof(ItemBow), "OnHeldInteractStop", nameof(BowTime));
    }
    private bool Patch(Type type, string name, string? prefix = null, string? postfix = null,
        string? finalizer = null, string? transpiler = null)
    {
        var target = AccessTools.DeclaredMethod(type, name);
        if (target is null)
        {
            api?.Logger.Warning("[vsslssmod] Skipped missing native hook {0}.{1}.", type.FullName, name);
            return false;
        }
        try
        {
            harmony.Patch(target,
                prefix: prefix is null ? null : new(typeof(NativeInjuryActionBridge), prefix),
                postfix: postfix is null ? null : new(typeof(NativeInjuryActionBridge), postfix),
                finalizer: finalizer is null ? null : new(typeof(NativeInjuryActionBridge), finalizer),
                transpiler: transpiler is null ? null : new(typeof(NativeInjuryActionBridge), transpiler));
            return true;
        }
        catch (Exception ex)
        {
            api?.Logger.Warning("[vsslssmod] Skipped native hook {0}.{1}: {2}: {3}.", type.FullName, name, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    internal static FunctionSnapshot? Functions(Entity entity)
    {
        try
        {
            if (entity is not EntityPlayer player || !entity.Alive
                || player.Player?.WorldData.CurrentGameMode != EnumGameMode.Survival) return null;
            var system = entity.Api.ModLoader.GetModSystem<SeraphModSystem>();
            if (entity.World.Side == EnumAppSide.Server) return system.Server?.ActionFunctions(entity);
            return entity.Api is ICoreClientAPI api && api.World?.Player is { } local
                && ReferenceEquals(local.Entity, entity)
                && system.ClientBridge is { HasFreshSnapshot: true, Snapshot: { } } client ? client.PredictedFunctions() : null;
        }
        catch { return null; }
    }
    private static ServerCoordinator? Server(Entity? entity) => entity?.World?.Side == EnumAppSide.Server
        ? entity.Api.ModLoader.GetModSystem<SeraphModSystem>().Server : null;
    internal static void Denied(Entity entity)
    {
        if (entity.World.Side == EnumAppSide.Server) Server(entity)?.DeniedLabor(entity);
        else entity.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge?.WarnExhaustion();
    }
    internal static double AppliedJumpMultiplier(double capacity, bool allowed)
        => !allowed ? 0 : capacity >= .9 - 1e-9 ? 1 : Math.Sqrt(Math.Clamp(capacity, 0, 1));

    private static void GroundStart(Entity entity, EntityControls controls, long ___lastJump, out GroundState? __state)
    {
        __state = null;
        try
        {
            if (entity is null || controls is null || entity is EntityPlayer { MountedOn: not null }
                || Functions(entity) is not { Movement: true } f) return;
            __state = new(controls.Jump, ___lastJump, f.Jump);
            if (!f.CanJump && controls.Flags is { Length: > 4 })
            {
                if (controls.Jump && f.BodyCanJump)
                {
                    if (Server(entity) is { } server) server.DeniedJump(entity);
                    else entity.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge?.WarnExhaustion("jump-cooldown");
                }
                controls.Flags[4] = false;
            }
        }
        catch
        {
            // A missing field or a third-party control implementation must not
            // turn a movement callback into a game-fatal exception.
            __state = null;
        }

    }
    private static Exception? GroundEnd(Entity entity, EntityControls controls, EntityPos pos, long ___lastJump,
        GroundState? __state, Exception? __exception)
    {
        try
        {
            if (__state is not null && controls is not null && pos is not null)
            {
                if (controls.Flags is { Length: > 4 }) controls.Flags[4] = __state.Jump;
                // Only a jump this module actually created; never scale falling, knockback or swimming velocity.
                if (__exception is null && ___lastJump != __state.LastJump && pos.Motion.Y > 0)
                {
                    // Preserve native one-block clearance for ordinary fatigue/nutrition (>=90%).
                    // Structural injuries and severe systemic illness retain their jump restriction.
                    pos.Motion.Y *= AppliedJumpMultiplier(__state.Capacity, true);
                    if (Server(entity) is { } server) server.ChargeAction(entity, ExertionKind.Jump);
                    else if (entity is EntityPlayer player) entity.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge?.PredictWork(player, ExertionKind.Jump, 1);
                }
            }
        }
        catch
        {
            // A compatibility hook must never turn its own bookkeeping error
            // into a client/server crash. Preserve an engine exception.
            return __exception;
        }
        return __exception;
    }
    private static void SprintSpeed(EntityAgent __instance, ref double __result)
    {
        try
        {
            if (__instance is not EntityPlayer player || player.MountedOn is not null
                || !(player.ServerControls.Sprint || player.Controls.Sprint || (player.CurrentControls & EnumEntityActivity.SprintMode) != 0)
                || player.Swimming || player.Controls.IsFlying
                || Functions(__instance) is not { Movement: true } f) return;
            // Replace only the native sprint increment. Walkspeed already contains the one mobility contribution.
            var sprint = f.CanSprint ? f.Sprint * f.PaceGain : 0;
            if (!f.CanSprint && (player.Controls.TriesToMove || player.ServerControls.TriesToMove)) Denied(player);
            __result *= (1 + (GlobalConstants.SprintSpeedMultiplier - 1) * sprint) / GlobalConstants.SprintSpeedMultiplier;
        }
        catch { }
    }
    private static bool AttackAnimation(EntityPlayer ___plrEntity)
    {
        if (Functions(___plrEntity) is not { Manipulation: true, CanUseActiveHand: false }) return true;
        Denied(___plrEntity); return false;
    }
    private static bool AttackStart(EntityAgent byEntity, EnumInteractMode mode)
    {
        try
        {
            if (mode != EnumInteractMode.Attack || Functions(byEntity) is not { Manipulation: true } functions) return true;
            if (!functions.CanUseActiveHand) Denied(byEntity);
            return functions.CanUseActiveHand;
        }
        catch { return true; }
    }
    private static bool MiningStart(IPlayer player, float remainingResistance, ref float dt, ref float __result, out float __state)
    {
        __state = 0;
        try
        {
            if (Functions(player.Entity) is not { Manipulation: true } f) return true;
            if (!f.CanUseActiveHand) { Denied(player.Entity); __result = remainingResistance; return false; }
            __state = dt;
            dt *= (float)f.Mining;
            return true;
        }
        catch { return true; }
    }
    private static void MiningEnd(IPlayer player, float remainingResistance, float __result, float __state)
    {
        // Protection, invalid targets and rejected native hits do not count as work.
        try
        {
            if (__state <= 0 || __result >= remainingResistance) return;
            if (Server(player.Entity) is { } server) server.MarkMining(player.Entity, __state);
            else if (player.CurrentBlockSelection is { } selection && player.Entity is EntityPlayer entity)
                entity.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge?.AcceptedMining(entity, selection, __state,
                    entity.World.BlockAccessor.GetBlock(selection.Position).Id);
        }
        catch { }
    }
    private static void BowTime(ref float secondsUsed, EntityAgent byEntity)
    {
        try
        {
            if (Functions(byEntity) is not { Manipulation: true } f) return;
            // Stop still runs its native cleanup and the pre-ammunition time check. No arrow is consumed on denial.
            if (!f.CanDrawBow) { Denied(byEntity); secondsUsed = 0; return; }
            secondsUsed *= (float)f.Bow;
        }
        catch { }
    }
    private static void ResetHitCycle(PlayerAnimationManager __instance, string animCode, string ___lastActiveHeldHitAnimation)
    {
        try { if (animCode == ___lastActiveHeldHitAnimation) HitCycles.Remove(__instance); } catch { }
    }
    private static void StopHitCycle(PlayerAnimationManager __instance, string? ___lastActiveHeldHitAnimation)
    {
        try { if (___lastActiveHeldHitAnimation is null) HitCycles.Remove(__instance); } catch { }
    }
    internal static bool ClaimHitCycle(PlayerAnimationManager manager)
    {
        var cycle = HitCycles.GetValue(manager, _ => new());
        if (cycle.Charged) return false;
        cycle.Charged = true;
        return true;
    }
    private static void AttackCycle(PlayerAnimationManager __instance, EntityPlayer ___plrEntity, string animCode)
    {
        try
        {
            if (string.IsNullOrEmpty(animCode)) return;
            var playerSelection = ___plrEntity.Player;
            if (playerSelection?.CurrentBlockSelection is not null && playerSelection.CurrentEntitySelection is null) return;
            if (Functions(___plrEntity) is { Manipulation: true })
            {
                AdrenalineAttackTiming.Tag(__instance, ___plrEntity, animCode);
                AdrenalineAttackTiming.Tag(___plrEntity.TpAnimManager, ___plrEntity, animCode);
                AdrenalineAttackTiming.Tag(___plrEntity.SelfFpAnimManager, ___plrEntity, animCode);
            }
            if (!ClaimHitCycle(__instance)) return;
            var player = ___plrEntity.Player;
            if (player?.CurrentBlockSelection is not null && player.CurrentEntitySelection is null) return;
            if (Functions(___plrEntity) is { Manipulation: true, CanUseActiveHand: true })
            {
                if (Server(___plrEntity) is { } server) server.ChargeAction(___plrEntity, ExertionKind.Attack);
                else ___plrEntity.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge?.AcceptedAttack(___plrEntity);
            }
        }
        catch { }
    }
    // Replace only native positive air recovery; underwater loss, Check(), and other mods remain active.
    private static IEnumerable<CodeInstruction> OxygenRecovery(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToArray();
        if (code.Count(i => i.opcode == OpCodes.Ldc_R4 && i.operand is float v && v == 10000f) != 1)
            throw new InvalidOperationException("Unsupported native oxygen recovery constant.");
        oxygenHookMatched = true;
        foreach (var instruction in code)
        {
            yield return instruction;
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && value == 10000f)
            {
                yield return new(OpCodes.Ldarg_0);
                yield return CodeInstruction.Call(typeof(NativeInjuryActionBridge), nameof(RecoveryRate));
            }
        }
    }
    private static float RecoveryRate(float nativeRate, EntityBehaviorBreathe behavior)
    {
        try
        {
            if (Server(behavior.entity) is { } server) return server.OwnsOxygen(behavior.entity) ? 0 : nativeRate;
            // Vanilla also predicts positive recovery on the client. The synchronized native
            // reservoir is authoritative while our local survival snapshot is fresh.
            return OxygenReplacementReady && Functions(behavior.entity) is { OxygenReplacement: true } ? 0 : nativeRate;
        }
        catch { return nativeRate; }
    }
    public void Dispose() { attackTiming.UnpatchAll("vsslssmod.adrenaline-attacks"); OxygenReplacementReady = false; harmony.UnpatchAll("vsslssmod.injury-actions"); }
}
