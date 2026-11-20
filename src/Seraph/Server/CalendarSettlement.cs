using HarmonyLib;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using System.Reflection;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private void NotifySleepDanger(Session s, BodyState body, FrameworkState frame)
    {
        if (s.Player.Entity.MountedOn is not BlockEntityBed
            || s.Player.Entity.GetBehavior<EntityBehaviorTiredness>()?.IsSleeping != true) return;
        // Warn once for each newly appearing risk. Bed rest and the calendar keep running.
        var risks = SleepRisks(s, body, frame);
        var added = risks & ~s.SleepDangerMask;
        s.SleepDangerMask = risks;
        if (added == 0) return;
        s.Player.SendIngameError("vsslssmod-sleep-danger", L(s.Player, "sleep-danger-warning"));
    }

    internal void ObserveNativeSleep(EntityBehaviorTiredness tired)
    {
        if (paused || tired.entity.World?.Side != EnumAppSide.Server
            || tired.entity is not EntityPlayer { Player: IServerPlayer player } entity
            || !Current(player, out var s)) return;
        s.SleepTimeline.Observe(api.World.Calendar.TotalHours, tired.IsSleeping,
            tired.IsSleeping ? EstimateBedQuality(entity) : 1);
        if (!tired.IsSleeping) s.SleepDangerMask = 0;
    }

    private void ResetSleepTimeline(Session s)
    {
        var entity = s.Player.Entity;
        var sleeping = entity.GetBehavior<EntityBehaviorTiredness>()?.IsSleeping == true;
        s.SleepTimeline.Reset(sleeping, sleeping ? EstimateBedQuality(entity) : 1);
    }

    private void RestoreNativeTiredness(Session s)
    {
        if (!OwnsPhysiology(s.Player.Entity)
            || s.Player.Entity.GetBehavior<EntityBehaviorTiredness>() is not { } tired) return;
        // Earlier versions projected up to 24 points; vanilla caps at half a day.
        var value = tired.Tiredness;
        tired.Tiredness = float.IsFinite(value) ? Math.Clamp(value, 0, api.World.Calendar.HoursPerDay / 2) : 0;
    }

    internal void ResetCalendarBaseline()
    {
        Seraph.World.WorldCalendarTime.Reset(api.World);
        lastCalendarHours = double.NaN;
        calendarDelta = 0;
        foreach (var s in sessions.Values)
        {
            s.Scheduler.Reset();
            ResetSleepTimeline(s);
            ClearFlowWindow(s);
        }
    }

    private int SleepRisks(Session s, BodyState body, FrameworkState frame) =>
        (body.CoreTemperatureC < 35.5 ? 1 : 0)
        | (ClinicalEffects.HeatStress(frame, body, s.Profile) ? 2 : 0)
        | (body.BloodVolumeL(s.Profile) < s.Profile.ReferenceBloodVolumeL * .9 ? 4 : 0)
        | (Hydration.Stage(body, s.Profile) > 0 ? 8 : 0)
        | (frame.OxygenKnown && frame.OxygenAvailability < .1
            || s.Player.Entity.WatchedAttributes.GetTreeAttribute("oxygen")?.GetBool("hasair", true) == false ? 16 : 0)
        | (frame.Vitals.Health < s.Framework.Vitals.Health || settings.Config.EnableClinicalEffects
            && ClinicalEffects.DamagePerSecond(frame, body, s.Profile, true) > 0 ? 32 : 0);

    private void ProjectSleep(Session s)
    {
        if (!OwnsPhysiology(s.Player.Entity)) return;
        if (s.Player.Entity.GetBehavior<EntityBehaviorTiredness>() is not { } tired) return;
        if (!tired.IsSleeping) s.SleepDangerMask = 0;
        else NotifySleepDanger(s, s.State, s.Framework);
    }

    private Seraph.Network.BedRestSnapshot? ProjectBedRest(Session s)
    {
        // A tooltip is a read-only estimate, also useful in creative mode where physiology is frozen.
        if (s.Environment is null) return null;
        var condition = PlayerCondition.Evaluate(s.Framework, s.State, s.Profile);
        return new() {
            EnvironmentEfficiency = EstimateSleepQuality(s.Player.Entity, s.Environment, 1)
                * (1 - .2 * Treatments.Pain(s.Framework, BloodCirculation.LactateCapacityG(s.State, s.Profile)))
                * (1 - .2 * Treatments.ComplaintSeverity(s.Framework, "dermatitis")),
            TreatmentSupport = Treatments.SleepQuality(s.Framework, 0),
            PressureRecoveryPerGameHour = 3600 * settings.Config.BedSleepPressureRecoveryMultiplier / settings.Config.SleepPressureRecoverySeconds,
            FatigueRecoveryPerGameHour = 3600 * (1 + Treatments.Relief(s.Framework, "fatigue") * .5)
                / settings.Config.FatigueSleepRecoverySeconds
        };
    }

    // All calendar batches consume the same clinical and dietary inputs.
    private StepInputs PrepareStepInputs(Session s, FrameworkState frame, BodyState body, bool sleeping, double bed, double power, double seconds)
    {
        var diet = frame.Nutrition;
        var adrenalineActive = frame.AdrenalineLevel > 0 && frame.AdrenalineRemainingSeconds > 0;
        var renal = settings.Config.EnableClinicalEffects ? BloodCirculation.RenalSupport(frame, body, s.Profile, seconds, settings.Config.Circulation) : 0;
        return new() {
            EnergyDemandMultiplier = NativeStatInputs.PhysiologyMultiplier(s.Player.Entity, "hungerrate"),
            HealingMultiplier = NativeStatInputs.PhysiologyMultiplier(s.Player.Entity, "healingeffectivness"),
            HeatAdaptation = frame.HeatAdaptation, ColdAdaptation = frame.ColdAdaptation,
            RenalSupport = renal, BasalMetabolismMultiplier = (sleeping ? .75 : IsSeatedRest(s) || s.ClosedEyeRest != ClosedEyeRestMode.None ? .85 : 1)
                * (1 + (settings.Config.Circulation.RenalBasalMultiplier - 1) * renal),
            DigestionGPerHour = settings.Config.DigestionGPerGameHour, Digestion = settings.Config.Digestion,
            Nutrition = diet, BowelTransitRoomG = Math.Max(0, BowelSettlement.MaximumPendingG - s.Interactions.PendingBowelUnits),
            ActivityPowerW = power * (adrenalineActive ? 2 : 1), IsSleeping = sleeping, IsResting = !sleeping && (IsSeatedRest(s) || s.ClosedEyeRest != ClosedEyeRestMode.None), BedQuality = bed,
            SleepQuality = sleeping ? Treatments.SleepQuality(frame, EstimateSleepQuality(s.Player.Entity, s.Environment!, bed)
                * (1 - .2 * Treatments.Pain(frame, BloodCirculation.LactateCapacityG(body, s.Profile))) * (1 - .2 * Treatments.ComplaintSeverity(frame, "dermatitis"))) : 0,
            SleepPressureRiseSeconds = settings.Config.SleepPressureRiseSeconds,
            SleepPressureRecoverySeconds = settings.Config.SleepPressureRecoverySeconds
                / (sleeping ? settings.Config.BedSleepPressureRecoveryMultiplier : 1),
            BleedLps = ClinicalBleeding(s, frame, body.ElapsedBioSeconds),
            MuscleProteinAvailableG = diet.Get("protein").Known ? diet.Get("protein").Reserve : 0,
            MuscleRecoverySupport = NutrientInteractions.Support(diet, "electrolytes")
                * (1 - frame.Fatigue * .5) * (1 - frame.Pain),
            DigestionEfficiency = settings.Config.EnableClinicalEffects ? BodyFunctionResolver.Digestion(frame, body, s.Profile) : 1,
            WaterAbsorptionEfficiency = settings.Config.EnableClinicalEffects ? BodyFunctionResolver.WaterAbsorption(frame) : 1,
            FeverTemperatureRiseC = settings.Config.EnableClinicalEffects ? DiseaseSyndromes.FeverSetpointC(frame) - 37 : 0,
            EntericWaterLossLps = settings.Config.EnableClinicalEffects
                ? ClinicalEffects.EntericWaterLossLPerGameHour(frame, settings.Config.EntericWaterLossLPerHour) / 3600 : 0
        };
    }

    private static NutritionState SettleNutrition(NutritionState diet, BodyState before, StepResult step, double seconds, double bowelTransitRoomG)
    {
        return step.Nutrition ?? throw new InvalidOperationException("Calendar settlement requires the shared digestion result.");
    }

    private WoundRecoveryResult SettleClinicalStep(Session s, FrameworkState before, FrameworkState proposed,
        BodyState body, StepInputs inputs, double seconds, BodyState previousBody, SimulationDiagnostics flows, double bowelSpaceG)
    {
        proposed = AdrenalineEffects.AdvanceGameTimeCooldown(proposed, seconds);
        proposed = proposed with { Vitals = proposed.Vitals.WithNutrition(proposed.Nutrition, body, s.Profile)
            .WithExternalMaximum(NativeStatInputs.ExternalHealth(s.Player.Entity)) };
        if (settings.Config.EnableClinicalEffects)
        {
            var syndromes = DiseaseSyndromes.Settle(proposed, body, s.Profile, flows.EntericWaterLossL, seconds, settings.Config.DiseaseExposure);
            proposed = syndromes.State; body = syndromes.Body;
        }
        var damage = settings.Config.Mode == "survival" && settings.Config.EnableClinicalEffects
            ? ClinicalEffects.DamagePerSecond(proposed, body, s.Profile, capabilities.MayWrite(Capability.ThermalDamage))
                * settings.Config.ClinicalDamageMultiplier / CalendarTimeScheduler.DefaultCalendarRate : 0;
        if (settings.Config.EnableClinicalEffects && proposed.Syndromes.Sepsis && settings.Config.Mode == "survival")
            damage = Math.Max(damage, settings.Config.DiseaseExposure.SepsisHpPerTenGameMinutes / 600 * settings.Config.ClinicalDamageMultiplier);
        var (syndromeFrame, syndromeTransit) = settings.Config.EnableClinicalEffects
            ? DiseaseSyndromes.Transit(proposed, seconds, bowelSpaceG) : (proposed, 0d);
        proposed = syndromeFrame;
        var recovery = WoundRecovery.Settle(before, proposed, body, s.Profile, seconds,
            damage == 0 && inputs.BleedLps == 0 && ClinicalEffects.EntericBurden(proposed) == 0
                && Hydration.Stage(body, s.Profile) == 0, inputs.HealingMultiplier);
        if (settings.Config.EnableClinicalEffects)
        {
            var filteredPoints = Math.Max(0, proposed.Nutrition.FilteredMicroPoints - before.Nutrition.FilteredMicroPoints);
            var waterWaste = Math.Max(0, proposed.Nutrition.AbsorbedWaterWasteMl - before.Nutrition.AbsorbedWaterWasteMl);
            recovery = recovery with { State = recovery.State with { Blood = recovery.State.Blood with {
                WasteMl = Math.Min(1e9, recovery.State.Blood.WasteMl + filteredPoints * 15 + waterWaste) } } };
            var circulation = BloodCirculation.Settle(recovery.State, previousBody, recovery.Body, s.Profile, flows,
                recovery.ProteinG, seconds, Math.Max(0, bowelSpaceG - syndromeTransit), true,
                settings.Config.Circulation, inputs.RenalSupport, inputs.IsSleeping ? 1 : s.RestFraction,
                realTimeExercise: true, sleeping: inputs.IsSleeping);
            recovery = recovery with { State = circulation.State, Body = circulation.Body,
                EnergyKJ = recovery.EnergyKJ + circulation.EnergyKJ, BowelG = circulation.BowelG + syndromeTransit };
        }
        var materials = DietaryMetabolism.FinishMaterials(recovery.State.Nutrition, recovery.Body, s.Profile,
            seconds, inputs.IsSleeping ? inputs.SleepQuality : inputs.IsResting ? .35 : 0,
            inputs.MuscleRecoverySupport, 1);
        recovery = recovery with { State = recovery.State with { Nutrition = materials.Nutrition },
            Body = materials.Body with { ElectrolyteBalance = materials.Nutrition.Get("electrolytes").Reserve / Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum },
            EnergyKJ = recovery.EnergyKJ + materials.GrowthEnergyKJ };
        if (settings.Config.EnableClinicalEffects)
        {
            // Same accepted recovery fuel split used by the public flow ledger. Never use
            // net body deltas: incoming protein can replenish glycogen during this step.
            var recoveryGlycogen = Math.Min(body.GlycogenEnergyKJ, recovery.EnergyKJ);
            var level = Ketosis.Advance(before.Clinical.Ketosis,
                Math.Clamp(recovery.Body.GlycogenEnergyKJ / s.Profile.GlycogenCapacityKJ, 0, 1),
                flows.GlycogenConsumedKJ + recoveryGlycogen,
                flows.FatConsumedKJ + recovery.EnergyKJ - recoveryGlycogen, flows.MuscleFuelKJ, seconds);
            recovery = recovery with { State = recovery.State with { Clinical = recovery.State.Clinical with { Ketosis = level } } };
        }
        if (damage > 0 && !proposed.Downed.Active) s.ClinicalDamageThisTick += damage * seconds;
        return damage > 0 && !proposed.Downed.Active ? recovery with { State = recovery.State with {
            Vitals = recovery.State.Vitals.Change(-damage * seconds) } } : recovery;
    }

}

/// <summary>Observes native sleep boundaries without changing the bed or acceleration clocks.</summary>
internal sealed class NativeCalendarBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.calendar");
    private static ServerCoordinator? coordinator;
    internal NativeCalendarBridge(ServerCoordinator server)
    {
        coordinator = server;
        foreach (var name in new[] { "cmdTimeSet", "cmdTimeAdd", "cmdTimeSetMonth" })
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Server.CmdTime"), name),
                postfix: new(typeof(NativeCalendarBridge), nameof(Adjusted)));
        harmony.Patch(AccessTools.PropertySetter(typeof(EntityBehaviorTiredness), nameof(EntityBehaviorTiredness.IsSleeping)),
            postfix: new(typeof(NativeCalendarBridge), nameof(SleepChanged)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockBed), nameof(BlockBed.OnBlockInteractStart)),
            prefix: new(typeof(NativeCalendarBridge), nameof(AdmitSleepPressure)));
    }
    private static void Adjusted() => coordinator?.ResetCalendarBaseline();
    private static void SleepChanged(EntityBehaviorTiredness __instance) => coordinator?.ObserveNativeSleep(__instance);
    private static bool AdmitSleepPressure(BlockBed __instance, IWorldAccessor world, IPlayer byPlayer,
        BlockSelection blockSel, ref bool __result)
    {
        if (world.Side != EnumAppSide.Server || byPlayer.Entity is not EntityPlayer entity
            || entity.GetBehavior("tiredness") is not EntityBehaviorTiredness { Tiredness: <= 8f }
            || coordinator?.AllowsBedWithoutNativeTiredness(entity) != true) return true;

        // Keep vanilla's access, occupancy and temporal-storm gates intact. Only take over
        // after those conditions are satisfied, skipping its native tiredness threshold.
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
        { __result = false; return false; }
        var opposite = BlockFacing.FromCode(__instance.LastCodePart()).Opposite;
        var bedPosition = __instance.LastCodePart(1) == "feet"
            ? blockSel.Position.AddCopy(opposite) : blockSel.Position;
        if (world.BlockAccessor.GetBlockEntity(bedPosition) is not BlockEntityBed bed
            || bed.MountedBy is not null) { __result = false; return false; }
        if (NativeTemporalStormSleepSetting(world) == 0
            && TemporalStormActive(world))
            return true;
        __result = entity.TryMount(bed);
        return false;
    }
    private static int NativeTemporalStormSleepSetting(IWorldAccessor world)
    {
        var config = typeof(IWorldAccessor).GetProperty("Config")?.GetValue(world);
        var getString = config?.GetType().GetMethod("GetString", [typeof(string), typeof(string)]);
        var raw = getString?.Invoke(config, ["temporalStormSleeping", "0"]) as string;
        return int.TryParse(raw, out var value) ? value : 0;
    }
    private static bool TemporalStormActive(IWorldAccessor world)
    {
        // Avoid importing the optional SkiaSharp-backed property signature at compile time.
        var type = AccessTools.TypeByName("Vintagestory.GameContent.SystemTemporalStability");
        if (type is null) return false;
        var getter = world.Api.ModLoader.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "GetModSystem" && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 0);
        if (getter is null) return false;
        var system = getter.MakeGenericMethod(type).Invoke(world.Api.ModLoader, null);
        if (system is null) return false;
        var value = system.GetType().GetProperty("StormStrength", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(system);
        return value switch
        {
            float strength => strength > 0f,
            double strength => strength > 0d,
            _ => false
        };
    }
    public void Dispose() { harmony.UnpatchAll("vsslssmod.calendar"); coordinator = null; }
}
