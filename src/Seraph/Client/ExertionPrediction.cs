using Seraph.Core.Framework;
using Seraph.Network;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seraph.Client;

public sealed partial class SeraphClientBridge
{
    private WorkPrediction? workPrediction;
    private FunctionSnapshot? exertionFunctions;
    private FunctionSnapshot? exhaustedFunctionsBase, exhaustedFunctions;
    private (double Labor, double Sprint, bool Exhausted, string Reason, bool JumpAllowed, double PaceGain) predictedLoads;
    private RunningPace runningPace = new();
    private ExertionOptions runningOptions = new();
    private HeldWorkLedger? predictedHeldWork;
    private List<WorkNotice>? workNotices;
    private string predictionEpoch = "";
    private int predictionLife;
    private long intentSequence, lastIntentSent, lastPredictionTick;
    private long predictedGrindingUntil;
    private long lastCooldownTick;
    private double lastDiscreteWorkAt = double.NegativeInfinity;
    internal bool DiscreteWorkAllowed() => PredictedFunctions() is not { Manipulation: true } f
        || f.CanUseActiveHand && api.World.ElapsedMilliseconds / 1000d - lastDiscreteWorkAt >= 1 / Math.Clamp(f.Mining, .05, 1) - 1;
    internal void PredictDiscreteWork(EntityPlayer entity, ExertionKind kind)
    {
        lastDiscreteWorkAt = api.World.ElapsedMilliseconds / 1000d;
        PredictWork(entity, kind, 1);
    }

    private void ResetWorkPrediction()
    {
        workPrediction = null; exertionFunctions = null; predictedHeldWork = null; workNotices = null;
        exhaustedFunctionsBase = exhaustedFunctions = null;
        predictionEpoch = ""; predictionLife = 0; intentSequence = 0;
        lastIntentSent = lastPredictionTick = predictedGrindingUntil = 0;
        lastCooldownTick = 0;
        lastDiscreteWorkAt = double.NegativeInfinity;
        runningPace = new(); runningOptions = new();
    }
    private void ReceiveExertion(ExertionState? state)
    {
        if (state is null || Snapshot is null) return;
        if (predictionEpoch != Snapshot.SessionEpoch || predictionLife != state.Life)
        {
            // Ignore a delayed packet from a previous life in the same connection.
            if (predictionEpoch == Snapshot.SessionEpoch && state.Life < predictionLife) return;
            ResetWorkPrediction(); pendingMining = null; nativeWorkSequence = 0; predictionEpoch = Snapshot.SessionEpoch; predictionLife = state.Life;
        }
        workPrediction ??= new();
        var options = new ExertionOptions { BurstSeconds = state.BurstSeconds, BurstTransitionSeconds = state.BurstTransitionSeconds,
            BurstRearmSeconds = state.BurstRearmSeconds, RunningSpeedGain = state.RunningSpeedGain,
            RunningStaminaPerSecond = state.RunningStaminaPerSecond, JoggingSpeedGain = state.JoggingSpeedGain,
            RunningSlowdownStamina = state.RunningSlowdownStamina, UnlockStamina = state.UnlockStamina,
            UnlockLactateRatio = state.UnlockLactateRatio };
        if (workPrediction.Reconcile(state.Sequence, state.Acknowledged, state.Stamina, state.Lactate,
            state.Capacity, state.Endurance, state.Exhausted, state.Credits,
            state.JumpCooldownRemainingSeconds, state.ExhaustedJumpCooldownSeconds, state.UnlockStamina, state.UnlockLactateRatio,
            options))
        {
            runningOptions = options with { UnlockStamina = state.UnlockStamina, UnlockLactateRatio = state.UnlockLactateRatio };
            runningPace.Restore(state.RunningHeld,
                state.RunningHeld && runningPace.Held ? Math.Max(runningPace.Elapsed, state.RunningElapsed) : state.RunningElapsed,
                state.RunningReleased, state.RunningBurst, state.RunningLimit);
            exertionFunctions = state.Functions; lastCooldownTick = api.World.ElapsedMilliseconds;
        }
    }
    internal FunctionSnapshot? PredictedFunctions()
    {
        var f = exertionFunctions ?? Snapshot?.Functions;
        if (f is null || workPrediction is not { Ready: true } prediction) return f;
        var reserves = prediction.Reserves();
        var labor = Exertion.LoadLimit(reserves.Stamina, reserves.Lactate / prediction.Capacity, f.Fatigue, f.SleepPressure).Value;
        var sprint = Exertion.LoadLimit(reserves.Stamina, reserves.Lactate / prediction.Capacity, f.Fatigue, f.SleepPressure, sprint: true).Value;
        var reason = !reserves.Exhausted ? "" : f.ExhaustionReason.Length > 0 ? f.ExhaustionReason : reserves.Reason;
        var jumpAllowed = f.BodyCanJump && (!reserves.Exhausted || prediction.JumpCooldownRemaining <= 1e-9);
        var paceGain = runningPace.Gain(runningOptions, reserves.Stamina, reserves.Lactate / prediction.Capacity, reserves.Exhausted);
        var next = (labor, sprint, reserves.Exhausted, reason, jumpAllowed, paceGain);
        if (ReferenceEquals(f, exhaustedFunctionsBase) && predictedLoads == next) return exhaustedFunctions;
        exhaustedFunctionsBase = f;
        predictedLoads = next;
        exhaustedFunctions = ProjectFunctions(f, labor, sprint, reserves.Exhausted, reason);
        exhaustedFunctions.PaceGain = paceGain;
        exhaustedFunctions.CanJump = jumpAllowed;
        return exhaustedFunctions;
    }
    internal static FunctionSnapshot ProjectFunctions(FunctionSnapshot f, double labor, double sprint, bool exhausted, string reason)
    {
        var laborRatio = labor / Math.Max(.001, f.LaborLoad);
        var sprintRatio = sprint / Math.Max(.001, f.SprintLoad);
        var mining = f.Mining * laborRatio; var bow = f.Bow * laborRatio;
        var sprintValue = f.Sprint * sprintRatio;
        return new() { Movement = f.Movement, Manipulation = f.Manipulation,
            Jump = f.Jump, Sprint = sprintValue, Mining = mining, Bow = bow, HandCapacity = f.HandCapacity * laborRatio,
            LaborLoad = labor, SprintLoad = sprint, Fatigue = f.Fatigue, SleepPressure = f.SleepPressure,
            CanJump = f.CanJump, BodyCanJump = f.BodyCanJump, CanSprint = f.CanSprint, PaceGain = f.PaceGain, MeleeAttackSpeed = f.MeleeAttackSpeed,
            CanDrawBow = f.CanDrawBow && bow >= .3 && !exhausted,
            CanUseActiveHand = f.CanUseActiveHand && mining >= .2 && !exhausted,
            ExhaustionReason = reason, OxygenReplacement = f.OxygenReplacement };
    }
    internal void PredictWork(EntityPlayer entity, ExertionKind kind, double seconds)
    {
        if (!HasFreshSnapshot || !ReferenceEquals(entity, api.World.Player?.Entity)
            || workPrediction is not { Ready: true } prediction || !double.IsFinite(seconds)) return;
        // Split long native steps into bounded notices; the prediction queue also bounds this loop.
        var seated = entity.Alive && entity.Controls.FloorSitting && !entity.Controls.TriesToMove
            && entity.MountedOn is null && !entity.Swimming;
        while (seconds > 0)
        {
            var step = Math.Min(1, seconds);
            if (prediction.Add(kind, step, seated) is not { } work) return;
            (workNotices ??= []).Add(new() { Sequence = work.Sequence, Kind = (int)work.Kind, Seconds = work.Seconds });
            seconds -= step;
        }
    }
    internal void PredictHeldWork(EntityPlayer entity, string source, double elapsed, ExertionKind kind)
        => PredictWork(entity, kind, (predictedHeldWork ??= new()).Advance(source, elapsed));
    internal void EndPredictedWork(string source, double elapsed = 0, ExertionKind kind = ExertionKind.Standing)
    {
        if (api.World.Player?.Entity is { } entity && predictedHeldWork is { } ledger)
        { PredictWork(entity, kind, ledger.Finish(source, elapsed)); ledger.End(source); }
    }
    internal void PredictGrinding(bool grinding)
        => predictedGrindingUntil = grinding ? api.World.ElapsedMilliseconds + 1000 : 0;

    private void PollExertion()
    {
        var now = api.World.ElapsedMilliseconds;
        var dt = lastPredictionTick > 0 ? Math.Clamp((now - lastPredictionTick) / 1000d, 0, .25) : 0;
        lastPredictionTick = now;
        var cooldownDt = lastCooldownTick > 0 ? Math.Max(0, (now - lastCooldownTick) / 1000d) : 0;
        lastCooldownTick = now;
        if (!HasFreshSnapshot || api.IsGamePaused || api.World.Player?.Entity is not { } entity
            || workPrediction is not { Ready: true }) return;
        workPrediction.Advance(cooldownDt);
        var controls = entity.Controls;
        // These are actual input flags. Native pre-gates never overwrite them permanently.
        var menu = api.Gui.OpenedGuis.Any(g => g.IsOpened() && g.PrefersUngrabbedMouse);
        var sprint = controls.Sprint || entity.ServerControls.Sprint || (entity.CurrentControls & EnumEntityActivity.SprintMode) != 0;
        var trigger = !menu && ((entity.MountedOn is not null ? ActivityCompatibility.TriggerHeld(entity) : sprint || controls.Jump)
            || controls.LeftMouseDown || controls.RightMouseDown
            || api.Input.MouseButton.Left || api.Input.MouseButton.Right);
        if (dt > 0 && PredictedFunctions() is { } f)
        {
            var moving = controls.TriesToMove || entity.ServerControls.TriesToMove
                || entity.OnGround && entity.Pos.Motion.X * entity.Pos.Motion.X + entity.Pos.Motion.Z * entity.Pos.Motion.Z > .0025;
            var reserves = workPrediction.Reserves();
            var run = runningPace.Advance(!menu && entity.MountedOn is null && !entity.Swimming && !controls.IsClimbing
                    && sprint && !controls.Sneak && !entity.ServerControls.Sneak && f.CanSprint,
                moving, dt, reserves.Stamina, reserves.Lactate / workPrediction.Capacity, reserves.Exhausted, runningOptions);
            if (!menu && entity.MountedOn is not null)
            {
                var observed = ActivityCompatibility.Observe(entity, f);
                if (observed.Active && Exertion.Strong(observed.Kind) && !workPrediction.Reserves().Exhausted)
                    PredictWork(entity, observed.Kind, dt);
            }
            if (!menu && entity.MountedOn is null && (controls.TriesToMove || entity.ServerControls.TriesToMove
                || entity.OnGround && entity.Pos.Motion.X * entity.Pos.Motion.X + entity.Pos.Motion.Z * entity.Pos.Motion.Z > .0025))
            {
                var kind = entity.Swimming ? ExertionKind.Swimming
                    : controls.IsClimbing ? ExertionKind.Climbing
                    : controls.Sneak || entity.ServerControls.Sneak ? ExertionKind.LowPosture
                    : sprint && f.CanSprint ? run : ExertionKind.Walking;
                if (Exertion.RunningMovement(kind) || kind == ExertionKind.Sprinting)
                {
                    var burstSeconds = runningPace.BurstChargeSeconds;
                    if (burstSeconds > 0) PredictWork(entity, ExertionKind.Sprinting, burstSeconds);
                    if (dt > burstSeconds) PredictWork(entity, kind, dt - burstSeconds);
                }
                else if (Exertion.Strong(kind) && !workPrediction.Reserves().Exhausted) PredictWork(entity, kind, dt);
            }
            if (f.CanDrawBow && entity.Attributes.GetInt("aiming") > 0) PredictWork(entity, ExertionKind.Bow, dt);
            if (f.CanUseActiveHand && now < predictedGrindingUntil) PredictWork(entity, ExertionKind.HeavyTool, dt);
        }
        if (now - lastIntentSent < 250) return;
        lastIntentSent = now;
        var batch = workNotices?.Take(32).ToArray() ?? [];
        if (batch.Length > 0) workNotices!.RemoveRange(0, batch.Length);
        channel.SendPacket(new WorkIntent { SessionEpoch = predictionEpoch, Life = predictionLife,
            Sequence = ++intentSequence, TriggerHeld = trigger, Operations = batch });
    }
}
