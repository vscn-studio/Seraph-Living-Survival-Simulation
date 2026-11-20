using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private const string MovementSource = "vsslssmod:function";
    private FrameworkKernel frameworkKernel = new();
    private readonly MedicalActionCoordinator medical = new();
    private CapabilityRegistry capabilities = null!;
    private SideEffectBarrier barrier = null!;
    private RuntimeDiagnostics diagnostics = null!;
    private static readonly IReadOnlyDictionary<string, DiseaseDefinition> DiseaseDefinitions = Pathogens.Definitions;

    private void StartFramework()
    {
        frameworkKernel = new(new() { FatigueRiseSeconds = settings.Config.FatigueRiseSeconds,
            FatigueRecoverySeconds = settings.Config.FatigueSleepRecoverySeconds,
            IntegrateBleeding = true, Pain = settings.Config.Pain, ThermalAdaptation = settings.Config.ThermalAdaptation, SeatedRecoverySeconds = settings.Config.FatigueSeatedRecoverySeconds });
        RefreshCapabilities();
        diagnostics = new(api, settings.Config.CsvMaximumMiB);
    }
    internal void ReportRuntimeCompatibility()
    {
        try { diagnostics.WriteCompatibility(capabilities, settings.Config); }
        catch (Exception ex) { api.Logger.Warning("[vsslssmod] Runtime report could not be written: {0}", ex.Message); }
    }
    private void RefreshCapabilities()
    {
        capabilities = CapabilityRegistry.Create(settings.Config, api.ModLoader.Mods.Select(m => (m.Info.ModID, m.Info.Name)));
        barrier = new(settings.Config.Mode, capabilities);
    }

    private void AttachObserver(Session session)
    {
        var entity = session.Player.Entity;
        if (entity is null || ReferenceEquals(entity, session.ObservedEntity)) return;
        if (session.ObservedEntity is { } previous)
        {
            RemoveEffects(session);
            if (session.Observer is not null) previous.RemoveBehavior(session.Observer);
        }
        CancelWash(session);
        session.Pressure = null;
        ClearClosedEyeRest(session);
        session.PositionKnown = false;
        session.LastDamageKind = null;
        session.LastHitPosition = "";
        session.LastResolvedRegion = "general";
        session.LastHitQuality = "unavailable";
        session.LastDamageAmount = 0;
        session.LastHurtCounter = entity.WatchedAttributes.GetInt("onHurtCounter");
        session.Observer = new(entity, (value, category) => OnSaturation(session, value, category), kind => session.LastDamageKind = kind);
        entity.AddBehavior(session.Observer);
        session.ObservedEntity = entity;
    }

    private void OnSaturation(Session session, float value, EnumFoodCategory category)
    {
        // Saturation has no ingredient identity or consumed quantity. It is NOT nutrition intake.
        // Verified solid/meal/liquid adapters alone may credit food composition.
    }

    private bool ApplyIntake(Session session, IntakeReceipt receipt)
    {
        if (session.Framework.RecentReceipts.Contains(receipt.Id, StringComparer.Ordinal)) return false;
        var body = session.Kernel.ApplyIntake(session.State, new() { WaterL = receipt.WaterL, EnergyKJ = receipt.EnergyKJ,
            ElectrolytesMg = receipt.ElectrolytesMg });
        if (!body.Accepted) return false;
        var frame = frameworkKernel.RecordIntake(session.Framework, receipt, body.AcceptedWaterL, body.AcceptedEnergyKJ);
        if (!frame.Accepted) return false;
        session.State = body.State;
        session.Framework = body.AcceptedWaterL > 0 || body.AcceptedEnergyKJ > 0
            ? RestFeedback.Relieve(frame.State, body.State.ElapsedBioSeconds) : frame.State;
        return true;
    }

    private void ObserveConfirmedHurt(Session session)
    {
        var entity = session.Player.Entity;
        var counter = entity.WatchedAttributes.GetInt("onHurtCounter");
        if (counter == session.LastHurtCounter) return;
        session.LastHurtCounter = counter;
        ClearClosedEyeRest(session);
        var kind = session.LastDamageKind;
        session.LastDamageKind = null;
        if (session.Fault is not null || !entity.Alive) return;
        // onHurt is written by Entity.ReceiveDamage after every behavior has received the mutable damage.
        // It is a game damage signal, not an anatomical injury measurement. At most one latest cue per tick.
        var amount = entity.WatchedAttributes.GetFloat("onHurt");
        if (!float.IsFinite(amount) || amount <= 0 || kind is null
            || ClassifyWound(new DamageSource { Type = kind.Value }) is not { } classification) return;
        if (OwnsPhysiology(entity))
        {
            if (kind is EnumDamageType.Suffocation or EnumDamageType.Injury) return;
            var adrenaline = frameworkKernel.ActivateAdrenaline(session.Framework, session.State, session.Profile,
                session.State.ElapsedBioSeconds, amount, bleeding: false);
            if (adrenaline.Accepted) { session.Framework = adrenaline.State; session.State = adrenaline.Body; }
            return;
        }
        if (!settings.Config.EnableWoundTracking || settings.Config.Mode == "observe") return;
        var woundKind = classification.Kind;
        // No inferred blood loss, fracture, permanent injury or additional HP damage from this generic cue.
        var result = frameworkKernel.RecordInjury(session.Framework, new($"cue:{session.LifeGeneration}:{counter}", woundKind,
            "general", Math.Clamp(amount / 20, 0.01, 1), InjuryEvidence.Unlocated, session.State.ElapsedBioSeconds));
        if (result.Accepted) session.Framework = result.State;
        Trace(session, "wound-cue:" + kind);
    }

    private FrameworkResult AdvanceFramework(Session session, BodyState body, StepInputs inputs, double dt, out int insectBites, FrameworkState? previous = null, double? paidActivityEnergyJ = null)
    {
        insectBites = 0;
        var entity = session.Player.Entity;
        var oxygen = entity.WatchedAttributes.GetTreeAttribute("oxygen");
        var current = oxygen?.GetFloat("currentoxygen") ?? float.NaN;
        var maximum = oxygen?.GetFloat("maxoxygen") ?? float.NaN;
        var known = float.IsFinite(current) && float.IsFinite(maximum) && maximum > 0;
        var context = new ActivityContext(inputs.ActivityPowerW, inputs.IsSleeping, inputs.IsResting,
            entity.MountedOn is not null, session.Teleported, known ? Math.Clamp(current / maximum, 0, 1) : 1, known)
        { HealingMultiplier = inputs.HealingMultiplier, PaidActivityEnergyJ = paidActivityEnergyJ, HasAir = oxygen?.GetBool("hasair", true) ?? true, SleepQuality = inputs.SleepQuality, BedQuality = inputs.BedQuality };
        var exposed = (previous ?? session.Framework) with { TreatmentEffectsEnabled = settings.Config.EnableClinicalEffects && settings.Config.Mode == "survival" };
        exposed = exposed with { Vitals = exposed.Vitals.WithExternalMaximum(NativeStatInputs.ExternalHealth(entity)) };
        exposed = Sanitation.Advance(exposed, session.Sanitation with { Moving = entity.ServerControls.TriesToMove }, dt, settings.Config.Sanitation);
        var ongoingExposure = new HashSet<string>();
        if (settings.Config.EnableClinicalEffects)
        {
            exposed = AddNearbyRespiratoryExposure(session, exposed, dt);
            var exposure = DiseaseExposure.AdvanceWithEvents(frameworkKernel, exposed, session.Environment!, dt, settings.Config.DiseaseExposure, ongoingExposure);
            exposed = exposure.State;
            insectBites = exposure.InsectBites;
        }
        if (settings.Config.EnableClinicalEffects)
            exposed = DiseaseExposure.AdvanceWounds(frameworkKernel, exposed, dt,
                settings.Config.WoundInfectionHazardPerSecond, ongoingExposure);
        exposed = ColdExposure.Advance(frameworkKernel, exposed, body, session.Profile, session.Environment!, dt);
        exposed = BloodCirculation.AddWaste(exposed, body, session.Profile, 0);
        var frame = frameworkKernel.Advance(exposed, body, session.Profile, session.Environment!, context, dt);
        return !frame.Accepted ? frame : frameworkKernel.ProgressDiseases(frame.State, DiseaseDefinitions, dt, ongoingExposure);
    }

    private FrameworkState AddNearbyRespiratoryExposure(Session receiver, FrameworkState state, double seconds)
    {
        if (seconds <= 0 || settings.Config.RespiratoryTransmissionUnitsPerSecond <= 0) return state;
        var target = receiver.Player.Entity;
        double? receiverProtection = null;
        foreach (var source in sessions.Values)
        {
            if (ReferenceEquals(source, receiver) || source.Fault is not null) continue;
            var entity = source.Player.Entity;
            if (entity is null || !entity.Alive || entity.Pos.Dimension != target.Pos.Dimension) continue;
            var dx = entity.Pos.X - target.Pos.X;
            var dy = entity.Pos.Y - target.Pos.Y;
            var dz = entity.Pos.Z - target.Pos.Z;
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance >= 8) continue;
            var infectious = source.Framework.Diseases.Where(d => (d.Pathogen is "respiratory" or "common-cold")
                && d.Phase is DiseasePhase.Symptomatic or DiseasePhase.Recovering)
                .ToArray();
            if (infectious.Length == 0) continue;
            receiverProtection ??= RespiratoryProtection(receiver);
            foreach (var infection in infectious)
            {
                var units = DiseaseExposure.RespiratoryContactUnits(distance, seconds,
                    receiver.Environment?.RoomVentilation ?? 0, infection.Burden, settings.Config.RespiratoryTransmissionUnitsPerSecond,
                    RespiratoryProtection(source), receiverProtection.Value);
                if (units > 0) state = ExposureBarriers.Add(frameworkKernel, state, ExposureRoute.Airborne,
                    new Dictionary<string, double> { [infection.Pathogen] = units }, "nearby-respiratory-carrier");
            }
        }
        return state;
    }

    private double RespiratoryProtection(Session session)
    {
        var protection = 0d;
        var inventory = session.Player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
        if (inventory is null) return 1;
        foreach (var slot in inventory)
        {
            var code = slot.Itemstack?.Collectible.Code?.ToString();
            if (code is not null) protection = Math.Max(protection,
                settings.Config.RespiratoryProtectionByCode.GetValueOrDefault(code));
        }
        return 1 - Math.Clamp(protection, 0, 1);
    }

    private static bool IsSeatedRest(Session session)
    {
        var entity = session.Player.Entity;
        return entity.Alive && entity.ServerControls.FloorSitting && !entity.ServerControls.TriesToMove
            && entity.MountedOn is null && !entity.Swimming && !session.Teleported;
    }

    private double EstimateBedQuality(EntityPlayer entity)
    {
        try
        {
            var block = entity.MountedOn is Vintagestory.GameContent.BlockEntityBed bed ? bed.Block
                : api.World.BlockAccessor.GetBlock(entity.Pos.AsBlockPos);
            return Seraph.World.BedRestProperties.Quality(block);
        }
        catch { /* Bed observation is optional; the neutral surface remains usable. */ }
        return .45;
    }

    private static double EstimateSleepQuality(EntityPlayer entity, EnvironmentSample environment, double bedQuality)
    {
        // One shared efficiency input for both sleep pressure and fatigue recovery.
        var temperature = Math.Clamp(1 - Math.Abs(environment.AirTemperatureC - 20) * .025, .45, 1);
        var ventilation = Math.Clamp(.82 + environment.RoomVentilation * .18, .82, 1);
        var pollution = Math.Clamp(1 - environment.AirPollution * .3, .7, 1);
        var sheltered = environment.RoomVentilation < .95 || environment.WindSpeedMps < 2 ? 1 : .92;
        return Math.Clamp(bedQuality * temperature * ventilation * pollution * sheltered, 0, 1);
    }

    internal void RecordActionActivity(Entity entity, double powerW, double durationSeconds, bool discrete = false)
    {
        if (!OwnsPhysiology(entity) || !double.IsFinite(powerW) || !double.IsFinite(durationSeconds)
            || powerW <= 0 || durationSeconds <= 0) return;
        var session = SessionFor(entity);
        var until = ServerSeconds + Math.Clamp(durationSeconds, .05, 2);
        session.ActionEnergy.Record(powerW, discrete ? ServerSeconds : ServerSeconds - durationSeconds,
            discrete ? ServerSeconds + durationSeconds : ServerSeconds);
        session.ActionPowerW = Math.Clamp(ServerSeconds <= session.ActionUntilSeconds ? Math.Max(session.ActionPowerW, powerW) : powerW, 0, 1000);
        session.ActionUntilSeconds = Math.Max(session.ActionUntilSeconds, until);
    }

    private static void ObservePosition(Session session)
    {
        var entity = session.Player.Entity;
        var pos = entity.Pos;
        session.Teleported = session.PositionKnown && (Math.Pow(pos.X - session.LastX, 2) + Math.Pow(pos.Y - session.LastY, 2)
            + Math.Pow(pos.Z - session.LastZ, 2) > 64);
        session.LastX = pos.X; session.LastY = pos.Y; session.LastZ = pos.Z; session.PositionKnown = true;
    }

    private void TickActionsAndEffects(Session session, bool mayAdvance)
    {
        var entity = session.Player.Entity;
        var pos = entity.Pos;
        if (!mayAdvance || settings.Config.Mode != "survival") { CancelWash(session, notify: true); EndPressure(session, "unavailable"); }
        ValidatePressure(session);
        if (mayAdvance && ReadyForAction(session.Player, out _)) TryAutoWash(session);
        if (session.Wash is not null && !WashStillValid(session))
        { CancelWash(session, notify: true); SendSnapshot(session); }
        var action = medical.Advance(session.Player.PlayerUID, ServerSeconds, session.LifeGeneration, entity.Alive && mayAdvance,
            pos.X, pos.Y, pos.Z, entity.ServerControls.TriesToMove || entity.MountedOn is not null || session.Teleported);
        if (action is not null)
        {
            var wash = session.Wash;
            var completed = action.Phase == MedicalActionPhase.Completed && CompleteWash(session);
            session.Wash = null;
            SendSnapshot(session);
            session.Player.SendMessage(GlobalConstants.GeneralChatGroup, WashMessage(session, completed ? "complete" : "cancelled", wash), EnumChatType.Notification);
            Trace(session, completed ? "medical-complete" : "medical-cancelled");
        }
        if (!mayAdvance || !IsSurvivalTestPlayer(session.Player))
        { RemoveEffects(session); return; }
        if (ServerSeconds - session.LastEffectSeconds < 1) return;
        session.LastEffectSeconds = ServerSeconds;
        var functions = EffectiveFunctions(session);
        NativeStatInputs.SetOwned(entity, "walkspeed", MovementSource,
            barrier.Allows(SideEffectKind.Stats, Capability.Movement) ? (float)(functions.Mobility - 1) : null);
        if (barrier.Allows(SideEffectKind.Stats, Capability.Manipulation))
            NativeStatInputs.SetOwned(entity, "meleeWeaponsDamage", MovementSource, (float)(functions.Attack - 1));
        else NativeStatInputs.SetOwned(entity, "meleeWeaponsDamage", MovementSource, null);
    }

    private bool ReadyForAction(IServerPlayer? player, out Session session)
    {
        session = null!;
        return player is not null && Current(player, out session) && settings.Config.Mode == "survival"
            && IsSurvivalTestPlayer(player)
            && session.Fault is null && !session.Deceased && player.Entity?.Alive == true && !paused;
    }

    /// <summary>Allow control-server administrators to run survival interactions from creative mode.</summary>
    internal static bool IsSurvivalTestPlayer(IServerPlayer player) =>
        player.WorldData.CurrentGameMode == EnumGameMode.Survival
        || player.HasPrivilege(Privilege.controlserver);
    private TextCommandResult PlayerCheck(IServerPlayer? player)
    {
        if (player is null || !Current(player, out var session)) return TextCommandResult.Error(L(player, "server-player-not-ready"));
        return TextCommandResult.Success(string.Join("\n", PublicLines(session)));
    }
    private string[] PublicLines(Session session)
    {
        var state = session.State;
        var frame = session.Framework;
        var waterDeficit = Math.Max(0, session.Profile.ReferenceBodyWaterL - state.BodyWaterL);
        var hoursPerDay = api.World.Calendar.HoursPerDay;
        var cue = SleepFeedback.Evaluate(state.SleepPressure, frame.Fatigue,
            hoursPerDay > 0 ? api.World.Calendar.TotalHours % hoursPerDay / hoursPerDay * 24 : 12);
        return [
            session.Fault is null ? L(session.Player, "public-title") : L(session.Player, "server-summary-paused"),
            L(session.Player, "public-mode", ModeName(session.Player)),
            L(session.Player, "public-water", Number(waterDeficit, "0.00"), Number(state.GutWaterL, "0.00")),
            L(session.Player, "public-fatigue", Number(frame.Stamina, "P0"), Number(frame.Fatigue, "P0"), Number(state.SleepPressure, "P0")),
            L(session.Player, "public-wounds", frame.Wounds.Length, Number(frame.Pain, "P0")),
            L(session.Player, "public-circadian", Number(cue.CircadianSleepCue, "P0")),
            L(session.Player, "public-advice", waterDeficit > 0.8 ? L(session.Player, "advice-drink")
                : frame.Fatigue > 0.6 || state.SleepPressure > 0.7 ? L(session.Player, "advice-sleep")
                : frame.Pain > 0.05 ? L(session.Player, "advice-wound") : L(session.Player, "advice-stable"))
        ];
    }

    private TextCommandResult CancelMedical(IServerPlayer? player)
    {
        if (player is null || !Current(player, out var session))
            return TextCommandResult.Error(L(player, "server-player-not-ready"));
        CancelWash(session);
        session.Pressure = null;
        SendSnapshot(session);
        return TextCommandResult.Success(L(player, "medical-cancelled"));
    }

    private TextCommandResult BeginMedical(IServerPlayer? player, string action)
    {
        if (!ReadyForAction(player, out var session) || !settings.Config.EnableMedicalActions )
            return TextCommandResult.Error(L(player, "action-unavailable"));
        if (action == "pressure") return TextCommandResult.Error(L(player, "medical-pressure-hold"));
        return TextCommandResult.Error(L(player, "medical-water-gesture"));
    }

    private TextCommandResult ChangeMode(IServerPlayer? player, string mode)
    {
        if (startupFault is not null) return TextCommandResult.Error(L(player, "action-save-failed"));
        var config = settings.Config with { Mode = mode };
        if (config.Validate() is not null) return TextCommandResult.Error(L(player, "mode-invalid"));
        if (!SaveAll()) return TextCommandResult.Error(L(player, "action-save-failed"));
        try
        {
            api.StoreModConfig(config, ConfigFile);
            foreach (var session in sessions.Values) { RemoveEffects(session); CancelWash(session); }
            settings.CommitMode(mode);
            RefreshCapabilities();
            ReportRuntimeCompatibility();
            foreach (var session in sessions.Values) SendSnapshot(session);
            return TextCommandResult.Success(L(player, "mode-changed", ModeName(player)));
        }
        catch (Exception ex) { api.Logger.Error("[vsslssmod] Mode change failed: {0}", ex); return TextCommandResult.Error(L(player, "action-save-failed")); }
    }

    private void RemoveEffects(Session session)
    {
        ClearGroundSquat(session);
        if ((session.ObservedEntity ?? session.Player.Entity) is { } entity)
        {
            NativeStatInputs.SetOwned(entity, "walkspeed", MovementSource, null);
            NativeStatInputs.SetOwned(entity, "meleeWeaponsDamage", MovementSource, null);
        }
    }
    private void CleanupSession(Session session)
    {
        if (session.Player.Entity is { } entity) smoke?.Ownership(entity, false);
        session.SmokeSampleValid = false;
        session.ClinicalWarnings.Reset(); session.PendingInsectBites = 0;
        if (session.Player.Entity?.MountedOn is Seraph.World.BlockEntityLatrine) session.Player.Entity.TryUnmount();
        session.LatrineDefecation = null;
        CancelWash(session);
        RemoveEffects(session);
        if (session.Observer is not null) session.ObservedEntity?.RemoveBehavior(session.Observer);
        session.Observer = null; session.ObservedEntity = null;
    }
    private void Trace(Session session, string lifecycle)
    {
        if (!settings.Config.TelemetryEnabled) return;
        var entity = session.Player.Entity;
        if (entity is null) return;
        diagnostics.Sample(session.Player.PlayerUID, lifecycle, session.LastDt, api.World.Calendar.TotalHours, calendarDelta,
            session.State.ElapsedBioSeconds, session.DroppedServerSeconds, session.Environment, session.Framework,
            entity.MountedOn is not null, entity.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0, session.Sheltered,
            string.Join("|", entity.SidedProperties.Behaviors.Take(64).Select(b => b.GetType().FullName)), session.VanillaProbe);
    }
}
