using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private static bool Moving(Vintagestory.API.Common.EntityPlayer e) => e.MountedOn is not null
        ? ActivityCompatibility.Observe(e).Active
        : e.ServerControls.TriesToMove || e.Controls.TriesToMove
            || e.OnGround && e.Pos.Motion.X * e.Pos.Motion.X + e.Pos.Motion.Z * e.Pos.Motion.Z > .0025;
    private static bool SprintIntent(Vintagestory.API.Common.EntityPlayer e) => e.ServerControls.Sprint || e.Controls.Sprint
        || (e.CurrentControls & EnumEntityActivity.SprintMode) != 0;

    private void WorkInterval(Session s, double start, double end)
    {
        s.WorkIntervals.Add(start, end);
        s.CalendarWorkIntervals.Add(start, end);
        s.RecoveryClock.RecordWork(end);
    }

    private bool JumpAllowed(Session s) => EffectiveFunctions(s).CanJump
        && (!s.SprintExhausted || s.JumpCooldownRemaining <= 1e-9);

    internal bool DiscreteWorkAllowed(Entity entity)
    {
        if (!OwnsPhysiology(entity)) return true;
        var s = SessionFor(entity);
        var speed = Math.Clamp(EffectiveFunctions(s).Mining, .05, 1);
        return !s.SprintExhausted && ServerSeconds - s.LastDiscreteWorkAt >= 1 / speed - 1;
    }
    private static BodyFunctions EffectiveFunctions(Session s)
    {
        var inputs = (s.Player.Entity.WatchedAttributes.GetFloat("intoxication"),
            s.Player.Entity.WatchedAttributes.GetFloat("psychedelic"), s.Interactions.BowelUnits);
        if (s.FunctionValues is { } cached && ReferenceEquals(s.FunctionFramework, s.Framework)
            && ReferenceEquals(s.FunctionBody, s.State) && ReferenceEquals(s.FunctionProfile, s.Profile)
            && s.FunctionInputs == inputs) return cached;
        s.FunctionFramework = s.Framework; s.FunctionBody = s.State; s.FunctionProfile = s.Profile; s.FunctionInputs = inputs;
        return s.FunctionValues = BodyFunctionResolver.Evaluate(s.Framework, s.State, s.Profile,
            inputs.Item1, inputs.Item2, inputs.Item3);
    }

    internal bool OwnsOxygen(Entity entity) => NativeInjuryActionBridge.OxygenReplacementReady
        && settings.Config.EnableInjuryActionEffects && OwnsPhysiology(entity);

    internal void RecordHeldWork(Entity entity, string source, double elapsed, ExertionKind kind)
    {
        if (!OwnsPhysiology(entity)) return;
        ChargeWork(entity, kind, SessionFor(entity).HeldWork.Advance(source, elapsed));
    }
    internal void EndHeldWork(Entity entity, string source, double elapsed = 0, ExertionKind kind = ExertionKind.Standing)
    {
        if (!OwnsPhysiology(entity)) return;
        var ledger = SessionFor(entity).HeldWork;
        ChargeWork(entity, kind, ledger.Finish(source, elapsed));
        ledger.End(source);
    }

    internal void MarkMining(Entity entity, double seconds)
    {
        if (!OwnsPhysiology(entity)) return;
        ChargeWork(entity, (entity as Vintagestory.API.Common.EntityPlayer)?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible.Tool
            == Vintagestory.API.Common.EnumTool.Axe ? ExertionKind.Logging : ExertionKind.Mining, seconds);
    }

    internal void ChargeWork(Entity entity, ExertionKind kind, double seconds, bool discrete = false)
    {
        if (!OwnsPhysiology(entity) || !double.IsFinite(seconds) || seconds <= 0) return;
        var s = SessionFor(entity);
        if (s.SprintExhausted && kind != ExertionKind.Jump) return;
        ApplyExertion(s, kind, seconds, false);
        // Discrete crafting is a short reserve impulse, while the native
        // transaction still occupies its normal one-second work interval.
        // Keep the interval full length so recovery and sleep settlement do
        // not treat a hand recipe as idle during the remaining transaction.
        var intervalSeconds = discrete ? Math.Max(1, seconds) : seconds;
        WorkInterval(s, discrete ? ServerSeconds : ServerSeconds - seconds, discrete ? ServerSeconds + intervalSeconds : ServerSeconds);
        if (discrete) s.LastDiscreteWorkAt = ServerSeconds;
        s.HandActivity = s.Activity = kind;
        s.StrongUntil = ServerSeconds + (discrete ? 1 : .25);
        RecordActionActivity(entity, Exertion.PowerW(kind) * Exertion.WorkMultiplier(kind, IsSeatedRest(s)), seconds, discrete);
    }

    internal void ChargeAction(Entity entity, ExertionKind kind)
    {
        if (!OwnsPhysiology(entity)) return;
        var s = SessionFor(entity);
        if (s.SprintExhausted && kind != ExertionKind.Jump) return;
        if (kind == ExertionKind.Attack)
        {
            if (ServerSeconds - s.LastAttackCharge < .25) return;
            s.LastAttackCharge = ServerSeconds;
        }
        if (kind == ExertionKind.Jump)
        {
            if (!JumpAllowed(s)) return;
            s.JumpCooldownRemaining = settings.Config.Exertion.ExhaustedJumpCooldownSeconds;
        }
        // A click or voxel batch is a brief impulse. Treating every accepted
        // transaction as a full second made hand work build lactate too quickly.
        var seconds = kind switch { ExertionKind.Crafting => .15, ExertionKind.HeavyTool => .2, _ => 1 };
        ChargeWork(entity, kind, seconds, true);
    }

    internal void RecordPlacedBlock(IServerPlayer player, ItemStack? stack)
    {
        if (player is null || stack?.Collectible is null || !OwnsPhysiology(player.Entity)) return;
        if (ActionFunctions(player.Entity) is not { Manipulation: true, CanUseActiveHand: true })
        {
            DeniedLabor(player.Entity);
            return;
        }
        ChargeAction(player.Entity, ExertionKind.Crafting);
    }

    private void TickExertion(Session s, bool active, double dt)
    {
        s.ActivityPowerW = 0;
        if (!active || !OwnsPhysiology(s.Player.Entity) || dt <= 0) return;
        var e = s.Player.Entity;
        s.JumpCooldownRemaining = Math.Max(0, s.JumpCooldownRemaining - dt);
        RecoverOxygen(s, dt);
        var trigger = (e.MountedOn is not null ? ActivityCompatibility.TriggerHeld(e) : SprintIntent(e) || e.ServerControls.Jump)
            || e.ServerControls.LeftMouseDown || e.ServerControls.RightMouseDown
            || s.IntentHeld && ServerSeconds - s.IntentAt <= 1;
        var wasLatched = s.SprintExhausted;
        s.SprintExhausted = Exertion.Exhausted(s.Framework.Stamina, s.Framework.Blood.LactateG,
            BloodCirculation.LactateCapacityG(s.State, s.Profile), s.SprintExhausted, trigger,
            s.Framework.ExertionRecoveryStage == RecoveryStage.Recovering, settings.Config.Exertion);
        if (wasLatched != s.SprintExhausted) PublishExertionTransition(s);
        var observed = ActivityCompatibility.Observe(e);
        if (e.MountedOn is not null || observed.Kind is ExertionKind.Climbing
            || observed.Kind is ExertionKind.Swimming && observed.Active)
        {
            s.RunningPace.Advance(false, false, dt, 0, 0, true, settings.Config.Exertion);
            var observedKind = observed.Active && !s.SprintExhausted ? observed.Kind : ExertionKind.Sitting;
            s.MovementActivity = observedKind;
            var observedStrong = Exertion.Strong(observedKind);
            if (observed.Active && !s.SprintExhausted) s.ActivityPowerW = observed.PowerW;
            if (observedStrong) WorkInterval(s, ServerSeconds - dt, ServerSeconds);
            SelectActivity(s, observedKind);
            ApplyExertion(s, observedKind, dt, false);
            if (e.Attributes.GetInt("aiming") > 0 && !s.SprintExhausted) ChargeWork(e, ExertionKind.Bow, dt);
            RecoverExertion(s, dt);
            return;
        }
        var moving = Moving(e);
        var bow = e.Attributes.GetInt("aiming") > 0 && !s.SprintExhausted;
        var sneak = e.ServerControls.Sneak || e.Controls.Sneak;
        var run = s.RunningPace.Advance(SprintIntent(e) && !sneak && EffectiveFunctions(s).CanSprint,
            moving, dt, s.Framework.Stamina, s.Framework.Blood.LactateG / BloodCirculation.LactateCapacityG(s.State, s.Profile),
            s.SprintExhausted, settings.Config.Exertion);
        var kind = e.Swimming && moving ? ExertionKind.Swimming
            : moving && !sneak && SprintIntent(e) && EffectiveFunctions(s).CanSprint ? run
            : moving && sneak ? ExertionKind.LowPosture
            : moving ? ExertionKind.Walking : IsSeatedRest(s) ? ExertionKind.Sitting : ExertionKind.Standing;
        s.MovementActivity = kind;
        var strong = Exertion.Strong(kind);
        if (strong || Exertion.RunningMovement(kind)) WorkInterval(s, ServerSeconds - dt, ServerSeconds);
        if (moving) s.ActivityPowerW = Exertion.MovementPowerW(kind, RunningPace.Fraction(s.Framework.Stamina,
            s.Framework.Blood.LactateG / BloodCirculation.LactateCapacityG(s.State, s.Profile), s.SprintExhausted, settings.Config.Exertion));
        var burstSeconds = kind == ExertionKind.Sprinting || Exertion.RunningMovement(kind) ? s.RunningPace.BurstChargeSeconds : 0;
        if (burstSeconds > 0) s.ActivityPowerW = (Exertion.MovementPowerW(ExertionKind.Sprinting) * burstSeconds
            + s.ActivityPowerW * (dt - burstSeconds)) / dt;
        SelectActivity(s, kind);
        if (bow) ChargeWork(e, ExertionKind.Bow, dt);
        if (burstSeconds > 0) ApplyExertion(s, ExertionKind.Sprinting, burstSeconds, false);
        if (dt > burstSeconds) ApplyExertion(s, kind, dt - burstSeconds, false);
        RecoverExertion(s, dt);
    }

    private void SelectActivity(Session s, ExertionKind movement)
        => s.Activity = ServerSeconds < s.StrongUntil && Exertion.PowerW(s.HandActivity) >= s.ActivityPowerW
            ? s.HandActivity : movement;

    private void RecoverExertion(Session s, double dt)
    {
        var rest = s.WorkIntervals.RestSeconds(ServerSeconds - dt, ServerSeconds);
        s.RestFraction = Math.Clamp(rest / dt, 0, 1);
        var e = s.Player.Entity;
        var moving = Moving(e);
        // Blocked attempts in the world are still exertion intent. UI clicks are not sent as intent.
        var held = (e.MountedOn is not null ? ActivityCompatibility.TriggerHeld(e) : e.ServerControls.Jump)
            || s.IntentHeld && ServerSeconds - s.IntentAt <= 1;
        if (held) s.RecoveryClock.RecordWork(ServerSeconds);
        var recovery = Math.Min(rest, s.RecoveryClock.Seconds(ServerSeconds - dt, ServerSeconds, settings.Config.Exertion.RestDelaySeconds));
        s.Framework = s.Framework with { ExertionRecoveryStage = rest <= 0 || held ? RecoveryStage.Working : recovery > 0 ? RecoveryStage.Recovering : RecoveryStage.Delay };
        if (recovery > 0) ApplyExertion(s, moving ? ExertionKind.Walking
            : e.ServerControls.Sneak || e.Controls.Sneak || IsSeatedRest(s)
                || e.GetBehavior<EntityBehaviorTiredness>()?.IsSleeping == true ? ExertionKind.Sitting : ExertionKind.Standing,
            recovery, true);
        if (rest > 0)
            s.Framework = BloodCirculation.RecoverExercise(s.Framework,
                moving ? ExertionKind.Walking : IsSeatedRest(s) || e.ServerControls.Sneak || e.Controls.Sneak
                    || e.GetBehavior<EntityBehaviorTiredness>()?.IsSleeping == true ? ExertionKind.Sitting : ExertionKind.Standing,
                rest, BloodCirculation.Pumping(s.Framework, s.State, s.Profile), settings.Config.Circulation);
    }

    private void RecoverOxygen(Session s, double seconds)
    {
        if (!s.Player.Entity.Alive || !OwnsOxygen(s.Player.Entity)
            || s.Player.Entity.GetBehavior<EntityBehaviorBreathe>() is not { MaxOxygen: > 0 } breath) return;
        breath.Oxygen = (float)(Exertion.Breathe(breath.Oxygen / breath.MaxOxygen, seconds,
            breath.HasAir, EffectiveFunctions(s).RespiratoryCapacity) * breath.MaxOxygen);
    }

    private static double NativeOxygen(Session s) => s.Player.Entity.GetBehavior<EntityBehaviorBreathe>() is { MaxOxygen: > 0 } b
        ? Math.Clamp(b.Oxygen / b.MaxOxygen, 0, 1) : 1;

    private void ApplyExertion(Session s, ExertionKind kind, double seconds, bool recover, double? oxygenRecoverySeconds = null)
    {
        var wasExhausted = s.SprintExhausted;
        var breath = s.Player.Entity.GetBehavior<EntityBehaviorBreathe>();
        var f = EffectiveFunctions(s);
        var beforeOxygen = NativeOxygen(s);
        var runningFraction = RunningPace.Fraction(s.Framework.Stamina,
            s.Framework.Blood.LactateG / BloodCirculation.LactateCapacityG(s.State, s.Profile), s.SprintExhausted, settings.Config.Exertion);
        var workMultiplier = Exertion.WorkMultiplier(kind, IsSeatedRest(s));
        var r = Exertion.Apply(s.Framework.Stamina, beforeOxygen, kind, seconds,
            breath?.HasAir == true, recover, s.SprintExhausted,
            PlayerCondition.Evaluate(s.Framework, s.State, s.Profile).StaminaRecoveryMultiplier,
            Exertion.StaminaCapacity(f.LaborEndurance, s.Framework.Nutrition), f.RespiratoryCapacity,
            oxygenRecoverySeconds, settings.Config.Exertion, workMultiplier, f.LaborEndurance);
        if (breath is { MaxOxygen: > 0 } && OwnsOxygen(s.Player.Entity))
            breath.Oxygen = Math.Clamp(breath.Oxygen + (float)((r.Oxygen - beforeOxygen) * breath.MaxOxygen), 0, breath.MaxOxygen);
        if (!recover && (Exertion.Strong(kind) || Exertion.RunningMovement(kind))) s.WorkAcknowledgements.Credit(kind, seconds, ServerSeconds);
        s.Framework = BloodCirculation.Charge(s.Framework with { Stamina = r.Stamina }, kind, seconds * workMultiplier,
            BloodCirculation.LactateCapacityG(s.State, s.Profile), f.LaborEndurance, runningFraction);
        if (!recover) s.Framework = ExerciseMuscles.Charge(s.Framework, kind, seconds, f.LaborEndurance, runningFraction);
        s.SprintExhausted = Exertion.Exhausted(r.Stamina, s.Framework.Blood.LactateG,
            BloodCirculation.LactateCapacityG(s.State, s.Profile), s.SprintExhausted, true, options: settings.Config.Exertion);
        if (s.SprintExhausted && s.Framework.ExertionReason.Length == 0)
            s.Framework = s.Framework with { ExertionReason = "stamina" };
        if (!s.SprintExhausted && s.Framework.ExertionReason.Length > 0) s.Framework = s.Framework with { ExertionReason = "" };
        if (wasExhausted != s.SprintExhausted) PublishExertionTransition(s);
    }

    private ExertionState ExertionProjection(Session s)
    {
        var credits = s.WorkAcknowledgements.Settle(ServerSeconds);
        return new() { Sequence = ++s.ExertionSequence, Life = s.ExertionLife,
            Acknowledged = s.WorkAcknowledgements.Acknowledged, Credits = credits,
            Stamina = s.Framework.Stamina, Lactate = s.Framework.Blood.LactateG,
            Capacity = BloodCirculation.LactateCapacityG(s.State, s.Profile),
            Endurance = Exertion.StaminaCapacity(EffectiveFunctions(s).LaborEndurance, s.Framework.Nutrition), Functions = ActionFunctions(s.Player.Entity), Exhausted = s.SprintExhausted,
            RecoveryStage = (int)s.Framework.ExertionRecoveryStage, RecoveryPolicyVersion = 1,
            JumpCooldownRemainingSeconds = s.JumpCooldownRemaining,
            ExhaustedJumpCooldownSeconds = settings.Config.Exertion.ExhaustedJumpCooldownSeconds,
            UnlockStamina = settings.Config.Exertion.UnlockStamina, UnlockLactateRatio = settings.Config.Exertion.UnlockLactateRatio,
            RunningHeld = s.RunningPace.Held, RunningElapsed = s.RunningPace.Elapsed, RunningReleased = s.RunningPace.Released,
            RunningBurst = s.RunningPace.Burst, BurstSeconds = settings.Config.Exertion.BurstSeconds,
            BurstTransitionSeconds = settings.Config.Exertion.BurstTransitionSeconds, BurstRearmSeconds = settings.Config.Exertion.BurstRearmSeconds,
            RunningSpeedGain = settings.Config.Exertion.RunningSpeedGain, RunningStaminaPerSecond = settings.Config.Exertion.RunningStaminaPerSecond,
            JoggingSpeedGain = settings.Config.Exertion.JoggingSpeedGain, RunningSlowdownStamina = settings.Config.Exertion.RunningSlowdownStamina,
            RunningLimit = s.RunningPace.Limit };
    }
    private void PublishExertionTransition(Session s)
    {
        if (s.HelloAccepted && channel is not null)
            channel.SendPacket(new ActivityLedger { SessionEpoch = s.Epoch, StateOnly = true,
                Exertion = ExertionProjection(s) }, s.Player);
    }
    private void OnWorkIntent(Vintagestory.API.Server.IServerPlayer player, WorkIntent packet)
    {
        if (!ReadyForAction(player, out var s) || packet.SessionEpoch != s.Epoch || packet.Life != s.ExertionLife
            || packet.Sequence <= s.IntentSequence || !OwnsPhysiology(player.Entity)) return;
        s.IntentSequence = packet.Sequence; s.IntentHeld = packet.TriggerHeld; s.IntentAt = ServerSeconds;
        foreach (var work in packet.Operations)
            s.WorkAcknowledgements.Notice(new(work.Sequence, (ExertionKind)work.Kind, work.Seconds), ServerSeconds);
    }

    private void PublishActivity(Session s, double dt)
    {
        var oxygen = NativeOxygen(s);
        if (s.HelloAccepted && dt > 0)
        {
            s.ActivityWindowWallSeconds += dt;
            s.ActivityWindowBioSeconds += s.SettledSeconds;
            s.ActivityWindowPaidEnergyJ += s.PaidEnergyJ;
            if (s.LedgerInitialized)
            {
                s.ActivityWindowStaminaDelta += s.Framework.Stamina - s.LastLedgerStamina;
                s.ActivityWindowOxygenDelta += oxygen - s.LastLedgerOxygen;
                s.ActivityWindowFatigueDelta += s.Framework.Fatigue - s.LastLedgerFatigue;
            }
        }
        if (s.HelloAccepted && s.LedgerInitialized && s.ActivityWindowWallSeconds >= 1)
        {
            var bioSeconds = s.ActivityWindowBioSeconds;
            var wallSeconds = s.ActivityWindowWallSeconds;
            s.ActivityRateEnergy = bioSeconds > 0 ? s.ActivityWindowPaidEnergyJ / 4184 * 3600 / bioSeconds : 0;
            s.ActivityRateStamina = s.ActivityWindowStaminaDelta / wallSeconds;
            s.ActivityRateOxygen = s.ActivityWindowOxygenDelta / wallSeconds;
            s.ActivityRateFatigue = bioSeconds > 0 ? s.ActivityWindowFatigueDelta * 3600 / bioSeconds : 0;
            s.ActivityWindowWallSeconds = s.ActivityWindowBioSeconds = s.ActivityWindowPaidEnergyJ = 0;
            s.ActivityWindowStaminaDelta = s.ActivityWindowOxygenDelta = s.ActivityWindowFatigueDelta = 0;
        }
        if (s.HelloAccepted && s.LedgerInitialized && channel is not null)
        {
            channel.SendPacket(new ActivityLedger {
                SessionEpoch = s.Epoch, Activity = s.Activity.ToString(),
                DemandMultiplier = 1 + s.DemandPowerW / (70 * 4184d / 3600),
                PaidKcalPerGameHour = s.ActivityRateEnergy,
                StaminaPerSecond = s.ActivityRateStamina, OxygenPerSecond = s.ActivityRateOxygen,
                FatiguePerGameHour = s.ActivityRateFatigue,
                Pressure = PressureProjection(s), DeniedReason = s.DeniedReason, Exertion = ExertionProjection(s)
            }, s.Player);
            s.DeniedReason = "";
        }
        s.LedgerInitialized = true;
        s.LastLedgerStamina = s.Framework.Stamina;
        s.LastLedgerOxygen = oxygen;
        s.LastLedgerFatigue = s.Framework.Fatigue;
        s.SettledSeconds = s.PaidEnergyJ = 0;
    }
}
