using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Seraph.World;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private sealed record HeldPressure(long Wound, long Generation, double Started, double LastHold, int HurtCounter,
        double X, double Y, double Z, int Dimension);
    private sealed record PendingWash(BlockPos Target, bool Bucket, int BlockId, BlockEntity? Container,
        int HurtCounter, ItemSlot Slot, ItemStack? Held, double LastHold, BlockEntityBathtub? Bath = null,
        string Kind = "clean");

    private static string WashActionKey(string kind, bool bath) => bath
        ? kind is "clean" or "clean-wound" ? "bathe-wound" : "bathe"
        : kind;
    private static string WashAction(PendingWash? wash) => WashActionKey(wash?.Kind ?? "clean", wash?.Bath is not null);
    private string WashMessage(Session s, string phase, PendingWash? wash) => L(s.Player, "wash-" + phase,
        L(s.Player, "world-action-" + WashAction(wash)), 5);
    private string WashBusy(Session s) => s.Wash is { } wash ? WashMessage(s, "busy", wash) : L(s.Player, "medical-busy");

    private static string WashKind(FrameworkState state, double bioSeconds, double residue = 0)
    {
        var body = WoundCare.NeedsBodyWash(state, residue);
        var wound = WoundCare.NeedsWoundWash(state, bioSeconds, residue);
        return body switch
        {
            true when wound => "clean",
            true => "clean-body",
            _ when wound => "clean-wound",
            _ => ""
        };
    }

    internal bool OwnsThermal(Entity entity) => OwnsPhysiology(entity) && capabilities.MayWrite(Capability.ThermalDamage);
    internal bool OwnsWoundCare(Entity entity) => OwnsPhysiology(entity) && settings.Config.EnableClinicalEffects && settings.Config.EnableMedicalActions
        && capabilities.MayWrite(Capability.BleedingDamage);

    internal bool OwnsBleeding(Entity entity) => OwnsPhysiology(entity) && capabilities.MayWrite(Capability.BleedingDamage);

    private double ClinicalBleeding(Session s, FrameworkState? frame = null, double? now = null) => settings.Config.EnableClinicalEffects
        && capabilities[Capability.BleedingDamage].Owner is CapabilityOwner.Vsslssmod or CapabilityOwner.ObserveOnly
        ? frameworkKernel.BleedLps(frame ?? s.Framework, now ?? s.State.ElapsedBioSeconds, s.Pressure?.Wound ?? 0) : 0;

    private void ValidatePressure(Session s)
    {
        if (s.Pressure is not { } p) return;
        var e = s.Player.Entity;
        var reason = !ReadyForAction(s.Player, out _) || !OwnsWoundCare(e) || p.Generation != s.LifeGeneration ? "unavailable"
            : ServerSeconds - p.LastHold > .75 ? "timeout"
            : e.WatchedAttributes.GetInt("onHurtCounter") != p.HurtCounter ? "hurt"
            : e.MountedOn is not null || e.Swimming || e.ServerControls.IsFlying || e.ServerControls.IsClimbing
                || e.ServerControls.TriesToMove || s.Teleported || e.Pos.Dimension != p.Dimension
                || Math.Pow(e.Pos.X - p.X, 2) + Math.Pow(e.Pos.Y - p.Y, 2) + Math.Pow(e.Pos.Z - p.Z, 2) > .09 ? "movement"
            : e.ServerControls.LeftMouseDown || e.ServerControls.RightMouseDown
                || e.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0
                || s.ClosedEyeRest != ClosedEyeRestMode.None || pendingTreatments.ContainsKey(s.Player.PlayerUID) ? "conflict" : "";
        if (reason.Length > 0) { EndPressure(s, reason); return; }
        if (s.Framework.Wounds.Any(w => w.EventSequence == p.Wound && WoundCare.Rate(w) > 0)) return;
        var next = WoundCare.Select(s.Framework);
        if (next == 0) EndPressure(s, "complete");
        else s.Pressure = p with { Wound = next };
    }

    private void EndPressure(Session s, string reason)
    {
        if (s.Pressure is null) return;
        s.Pressure = null;
        s.PressureReason = reason;
    }

    private PressureStatus PressureProjection(Session s)
    {
        var p = s.Pressure;
        return new() { Sequence = ++s.PressureSequence, ActionId = s.PressureActionId, Active = p is not null,
            Region = p is null ? "general" : s.Framework.Wounds.FirstOrDefault(w => w.EventSequence == p.Wound)?.Region ?? "general",
            ElapsedSeconds = p is null ? 0 : Math.Max(0, ServerSeconds - p.Started),
            BeforeMlPerMinute = s.Framework.Wounds.Sum(w => WoundCare.Rate(w)) * 60000,
            EffectiveMlPerMinute = ClinicalBleeding(s) * 60000, Reason = s.PressureReason };
    }

    private TextCommandResult BeginPressure(Session s)
    {
        if (!ReadyForAction(s.Player, out _) || !OwnsWoundCare(s.Player.Entity))
            return TextCommandResult.Error(L(s.Player, "medical-owner-unavailable"));
        var wound = WoundCare.Select(s.Framework);
        if (wound == 0)
            return TextCommandResult.Error(L(s.Player, "medical-no-bleed"));
        var e = s.Player.Entity;
        if (e.ServerControls.TriesToMove || e.MountedOn is not null || e.Swimming || e.ServerControls.IsFlying
            || e.ServerControls.IsClimbing || e.ServerControls.LeftMouseDown || e.ServerControls.RightMouseDown
            || e.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0)
            return TextCommandResult.Error(L(s.Player, "pressure-movement"));
        CancelCompetingActions(s);
        s.PressureReason = "";
        s.Pressure = new(wound, s.LifeGeneration, ServerSeconds, ServerSeconds,
            e.WatchedAttributes.GetInt("onHurtCounter"), e.Pos.X, e.Pos.Y, e.Pos.Z, e.Pos.Dimension);
        return TextCommandResult.Success(L(s.Player, "medical-pressure-start"));
    }

    // Called only after the replacement action has passed its preflight checks.
    private void CancelCompetingActions(Session s)
    {
        EndPressure(s, "conflict"); CancelWash(s, notify: true); CancelTreatment(s.Player); ClearClosedEyeRest(s);
        s.GroundDrink = null; s.Defecation = null;
        if (s.LatrineDefecation is { } latrine && s.Player.Entity.MountedOn == latrine.Latrine) s.Player.Entity.TryUnmount();
        s.LatrineDefecation = null;
        if (s.WellBucketRaise is { } well) (api.World.BlockAccessor.GetBlockEntity(well.Head) as BlockEntityWellHead)?.ClearRaiseProgress();
        s.WellBucketRaise = null;
        ClearGroundSquat(s);
    }

    private BlockPos? ImmersionTarget(Session s)
    {
        var pos = s.Player.Entity.Pos.AsBlockPos;
        foreach (var test in new[] { pos, pos.UpCopy() })
        {
            if (WaterInteractionTarget.IsSurfaceWater(api.World, test)
                && api.World.Claims.TryAccess(s.Player, test, EnumBlockAccessFlags.Use)) return test;
        }
        var container = WaterTarget(s, readOnly: false);
        if (container is not null && WaterInteractionTarget.IsWaterContainer(api.World, container)) return container;
        return null;
    }

    private WaterSource? WashSource(Session s, BlockPos target) => SourceAt(s, false, target)
        is { Batch.VolumeL: >= .25, Batch.TemperatureC: >= 0 and <= 45 } water ? water : null;

    private static bool BathPosture(EntityPlayer entity) => entity.ServerControls.ShiftKey
        || entity.ServerControls.Sneak || entity.ServerControls.FloorSitting;

    private BlockEntityBathtub? OccupiedBath(Session s)
    {
        var entity = s.Player.Entity;
        var pos = entity.Pos.AsBlockPos;
        for (var dy = 0; dy >= -1; dy--)
            if (BlockBathtub.GetMaster(api.World, pos.AddCopy(0, dy, 0)) is { } bath
                && bath.Contains(entity.Pos.XYZ, entity.Pos.Dimension)
                && api.World.Claims.TryAccess(s.Player, bath.Pos, EnumBlockAccessFlags.Use)) return bath;
        return null;
    }

    private WaterSource? CurrentWashSource(Session s)
    {
        if (s.Wash is not { } wash) return null;
        if (wash.Bath is not { } bath) return WashSource(s, wash.Target);
        if (bath.Water is not { VolumeL: >= 40, TemperatureC: >= 0 and <= 45 } batch) return null;
        WaterBatch? before = null;
        return new(batch, "water", amount => { before = bath.Water; return bath.TryConsume(amount, out _); },
            () => { if (before is not null) bath.RestoreWater(before); });
    }

    private void CancelWash(Session s, bool notify = false)
    {
        var wash = s.Wash;
        medical.Cancel(s.Player.PlayerUID);
        s.Wash = null;
        s.ProduceWash = null;
        if (notify && wash is not null && !s.Deceased && s.Player.Entity?.Alive == true)
            s.Player.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                WashMessage(s, "cancelled", wash), EnumChatType.Notification);
    }

    private TextCommandResult BeginWash(Session s, bool bucket)
    {
        if (!ReadyForAction(s.Player, out _) || !settings.Config.EnableMedicalActions)
            return TextCommandResult.Error(L(s.Player, "action-unavailable"));
        var entity = s.Player.Entity;
        if (!entity.ServerControls.ShiftKey) return TextCommandResult.Error(L(s.Player, "medical-water-gesture"));
        var target = bucket ? WaterTarget(s) : ImmersionTarget(s);
        if (target is null || bucket && (!(WaterInteractionTarget.IsWaterBucket(api.World, target)
                || WaterInteractionTarget.IsWaterContainer(api.World, target))
            || s.Player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible is BlockLiquidContainerBase)
            || WashSource(s, target) is not { } source)
            return TextCommandResult.Error(L(s.Player, "water-required"));
        var kind = WashKind(s.Framework, s.State.ElapsedBioSeconds, Sanitation.WaterResidue(source.Batch));
        if (kind.Length == 0)
            return TextCommandResult.Error(L(s.Player, "medical-no-wash-needed"));
        if (entity.MountedOn is not null || entity.ServerControls.TriesToMove)
            return TextCommandResult.Error(L(s.Player, "must-rest"));
        if (medical.Get(s.Player.PlayerUID) is not null) return TextCommandResult.Error(WashBusy(s));
        CancelCompetingActions(s);
        var p = entity.Pos;
        if (!medical.Begin(new(Guid.NewGuid().ToString("N"), s.Player.PlayerUID, s.Player.PlayerUID, "clean",
            s.LifeGeneration, ServerSeconds, 5, p.X, p.Y, p.Z, MedicalActionPhase.Pending)))
            return TextCommandResult.Error(WashBusy(s));

        var slot = s.Player.InventoryManager.ActiveHotbarSlot;
        s.Wash = new(target.Copy(), bucket, api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Solid).Id,
            api.World.BlockAccessor.GetBlockEntity(target), entity.WatchedAttributes.GetInt("onHurtCounter"), slot, slot.Itemstack, ServerSeconds, null, kind);
        return TextCommandResult.Success(WashMessage(s, "start", s.Wash));
    }

    private void TryAutoWash(Session s)
    {
        var entity = s.Player.Entity;
        var bath = OccupiedBath(s);
        var gesture = bath is not null ? BathPosture(entity) : entity.ServerControls.ShiftKey && ImmersionTarget(s) is not null;
        if (!gesture) { s.WaterWashLatched = false; return; }
        if (s.ClosedEyeRest != ClosedEyeRestMode.None || pendingTreatments.ContainsKey(s.Player.PlayerUID) || s.LatrineDefecation is not null
            || s.WaterWashLatched || s.Wash is not null || s.ProduceWash is not null || s.Pressure is not null || s.GroundDrink is not null
            || s.Defecation is not null || s.WellBucketRaise is not null || entity.ServerControls.TriesToMove
            || entity.ServerControls.RightMouseDown || entity.ServerControls.LeftMouseDown
            || !WoundCare.NeedsWash(s.Framework, s.State.ElapsedBioSeconds)) return;
        var result = bath is null ? BeginWash(s, bucket: false) : BeginBathWash(s, bath);
        s.WaterWashLatched = true; // One attempt per Shift/immersion gesture, including unsuitable water.
        s.Player.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup, result.StatusMessage, EnumChatType.Notification);
        SendSnapshot(s);
    }

    private bool WashStillValid(Session s)
    {
        if (s.Wash is not { } wash || medical.Get(s.Player.PlayerUID) is null) return false;
        var e = s.Player.Entity;
        if (!ReadyForAction(s.Player, out _) || !settings.Config.EnableMedicalActions
            || (wash.Bath is null ? !e.ServerControls.ShiftKey : !BathPosture(e))
            || e.ServerControls.LeftMouseDown || e.WatchedAttributes.GetInt("onHurtCounter") != wash.HurtCounter
            || e.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0
            || !ReferenceEquals(s.Player.InventoryManager.ActiveHotbarSlot, wash.Slot)
            || !ReferenceEquals(wash.Slot.Itemstack, wash.Held)
            || CurrentWashSource(s) is null) return false;
        if (wash.Bath is { } bath) return !e.ServerControls.RightMouseDown
            && ReferenceEquals(OccupiedBath(s), bath) && ReferenceEquals(api.World.BlockAccessor.GetBlockEntity(wash.Target), bath);
        if (!wash.Bucket) return !e.ServerControls.RightMouseDown && ImmersionTarget(s)?.Equals(wash.Target) == true;
        return ServerSeconds - wash.LastHold <= .75 && WaterTarget(s)?.Equals(wash.Target) == true
            && (WaterInteractionTarget.IsWaterBucket(api.World, wash.Target)
                || WaterInteractionTarget.IsWaterContainer(api.World, wash.Target))
            && api.World.BlockAccessor.GetBlock(wash.Target, BlockLayersAccess.Solid).Id == wash.BlockId
            && ReferenceEquals(api.World.BlockAccessor.GetBlockEntity(wash.Target), wash.Container);
    }

    private bool CompleteWash(Session s)
    {
        var water = CurrentWashSource(s);
        if (water is null) return false;
        var residue = Sanitation.WaterResidue(water.Batch);
        if (!WoundCare.NeedsWash(s.Framework, s.State.ElapsedBioSeconds, residue)) return false;
        var result = frameworkKernel.Treat(s.Framework, "clean", s.State.ElapsedBioSeconds);
        if (!result.Accepted) return false;
        var before = s.Framework;
        var bodyBefore = s.State;
        var bath = s.Wash?.Bath;
        var dirty = residue >= .1;
        var openWounds = result.State.Wounds.Count(w => w.Healing < 1 && WoundCare.Open(w.Kind, w.BleedLps));
        var woundAgents = water.Batch.PathogensPerL.Where(p => Pathogens.Wound(p.Key))
            .ToDictionary(p => p.Key, p => p.Value * .25 / Math.Max(1, openWounds));
        var next = result.State with {
            Dirtiness = Math.Max(residue, bath is null ? result.State.Dirtiness : 0),
            CleanRemainingGameHours = dirty ? before.CleanRemainingGameHours : settings.Config.Sanitation.CleanGameHours,
            BathedRemainingGameHours = !dirty && bath is not null ? settings.Config.Sanitation.BathedGameHours : before.BathedRemainingGameHours,
            SkinPathogens = PathogenLoads.Add(result.State.SkinPathogens, PathogenLoads.Scale(water.Batch.PathogensPerL, .25)),
            Wounds = result.State.Wounds.Select(w => w with {
                Pathogens = w.Healing < 1 && WoundCare.Open(w.Kind, w.BleedLps)
                    ? PathogenLoads.Add(w.Pathogens, woundAgents) : w.Pathogens,
                Contamination = WoundCare.Open(w.Kind, w.BleedLps) ? Math.Max(w.Contamination, residue) : w.Contamination,
                CleanUntilBioSeconds = dirty ? 0 : w.CleanUntilBioSeconds }).ToArray() };
        next = ExposureBarriers.Wash(next);
        var skinAgents = water.Batch.PathogensPerL.Where(p => Pathogens.All.Any(a => a.Id == p.Key && a.Route == ExposureRoute.Skin))
            .ToDictionary(p => p.Key, p => ExposureBarriers.ContactUnits(p.Value, .25));
        next = ExposureBarriers.Add(frameworkKernel, next, ExposureRoute.Skin, skinAgents, "washing-water-contact");
        if (bath is not null && water.Batch.TemperatureC < 10)
        {
            var cold = Math.Clamp((10 - water.Batch.TemperatureC) / 30, 0, 1);
            s.State = s.State with
            {
                SkinTemperatureC = Math.Max(-20, s.State.SkinTemperatureC - 1.25 * cold),
                CoreTemperatureC = Math.Max(25, s.State.CoreTemperatureC - .12 * cold)
            };
        }
        var soapUsed = bath is not null && bath.TrySpendSoapCharge();
        if (soapUsed)
        {
            next = SoapCare.CleanAll(next, s.State.ElapsedBioSeconds);
        }
        if (!water.Take(bath is null ? .25 : 10))
        {
            water.Restore();
            if (soapUsed) bath!.RestoreSoapCharge();
            return false;
        }
        try
        {
            if (bath is not null)
            {
                var removed = PathogenLoads.Scale(before.SkinPathogens, .9);
                foreach (var wound in before.Wounds) removed = PathogenLoads.Add(removed, PathogenLoads.Scale(wound.Pathogens, .5));
                bath.AcceptBathWaste(Math.Max(0, before.Dirtiness - next.Dirtiness), removed);
            }
            s.Framework = next;
            if (!Save(s)) { s.Framework = before; s.State = bodyBefore; water.Restore(); if (soapUsed) bath!.RestoreSoapCharge(); return false; }
        }
        catch
        {
            s.Framework = before;
            s.State = bodyBefore;
            water.Restore();
            if (soapUsed) bath!.RestoreSoapCharge();
            throw;
        }
        s.Wash = null; // A completed transaction cannot be submitted twice.
        if (dirty) Notify(s, "medical-dirty-water");
        if (soapUsed) Notify(s, "bathtub-soap-wash");
        return true;
    }

    private TextCommandResult BeginBathWash(Session s, BlockEntityBathtub bath)
    {
        if (!ReadyForAction(s.Player, out _) || !settings.Config.EnableMedicalActions)
            return TextCommandResult.Error(L(s.Player, "action-unavailable"));
        if (bath.Water is not { VolumeL: >= 40, TemperatureC: >= -20 and <= 45 } water)
            return TextCommandResult.Error(L(s.Player, "water-required"));
        var residue = Sanitation.WaterResidue(water);
        if (!WoundCare.NeedsWash(s.Framework, s.State.ElapsedBioSeconds, residue))
            return TextCommandResult.Error(L(s.Player, "medical-no-wash-needed"));
        var entity = s.Player.Entity;
        if (!BathPosture(entity) || entity.MountedOn is not null || !ReferenceEquals(OccupiedBath(s), bath))
            return TextCommandResult.Error(L(s.Player, "must-rest"));
        if (medical.Get(s.Player.PlayerUID) is not null) return TextCommandResult.Error(WashBusy(s));
        CancelCompetingActions(s);
        var pos = entity.Pos;
        if (!medical.Begin(new(Guid.NewGuid().ToString("N"), s.Player.PlayerUID, s.Player.PlayerUID, "clean",
            s.LifeGeneration, ServerSeconds, 5, pos.X, pos.Y, pos.Z, MedicalActionPhase.Pending)))
            return TextCommandResult.Error(WashBusy(s));
        var slot = s.Player.InventoryManager.ActiveHotbarSlot;
        s.Wash = new(bath.Pos.Copy(), false, bath.Block.Id, bath, entity.WatchedAttributes.GetInt("onHurtCounter"),
            slot, slot.Itemstack, ServerSeconds, bath, WashKind(s.Framework, s.State.ElapsedBioSeconds, residue));
        return TextCommandResult.Success(WashMessage(s, "start", s.Wash));
    }

    internal long SelectBandageWound(Entity entity) => OwnsWoundCare(entity) ? WoundCare.SelectForCare(SessionFor(entity).Framework) : 0;
    internal long SelectPoulticeWound(Entity entity) => OwnsWoundCare(entity)
        ? SessionFor(entity).Framework.Wounds.Where(w => w.Healing < 1).OrderByDescending(BodyFunctionResolver.Remaining)
            .Select(w => w.EventSequence).FirstOrDefault() : 0;
    internal bool ApplyConsumedPoultice(Entity entity, long wound, double strength, bool antiseptic)
    {
        if (!OwnsWoundCare(entity) || !entity.Alive) return false;
        var s = SessionFor(entity);
        var definition = new TreatmentDefinition { TimeUnitVersion = 1,
            Id = "native-poultice", Group = "native-poultice", Route = TreatmentRoute.Topical, RequiresWound = true,
            DelaySeconds = 0, DurationSeconds = 10800, Relief = .3, RecoveryBonus = .5,
            Symptoms = ["pain"], Pathogens = antiseptic ? ["wound-staph", "wound-local"] : [],
            WoundRepair = Math.Clamp(strength / 7, .2, 1), CleanFraction = antiseptic ? .5 : 0, Hemostasis = 1
        };
        var targets = s.Framework.Wounds.Where(w => w.Healing < 1).Select(w => w.EventSequence).ToArray();
        if (targets.Length == 0 || !targets.Contains(wound)) return false;
        var frame = s.Framework;
        foreach (var target in targets)
        {
            var treated = Treatments.Apply(frame, definition, s.Epoch + ":poultice:" + target + ":" + (frame.EventSequence + 1),
                "game:poultice", TreatmentRoute.Topical, s.State.ElapsedBioSeconds, target);
            if (!treated.Accepted) return false;
            frame = treated.State;
        }
        // One consumed item is one dose even though its full local effect reaches each wound.
        var repeats = Treatments.RecentDoses(s.Framework, definition.Group, s.State.ElapsedBioSeconds);
        frame = frame with {
            DrugToxicBurden = Math.Clamp(s.Framework.DrugToxicBurden + definition.Toxicity + (repeats >= 2 ? .2 : 0), 0, 1),
            AlcoholBurden = Math.Clamp(s.Framework.AlcoholBurden + definition.AlcoholL * 20, 0, 1),
            TreatmentDoses = s.Framework.TreatmentDoses.Where(d => d.BioSeconds > s.State.ElapsedBioSeconds - 18000)
                .TakeLast(63).Append(new TreatmentDose(definition.Group, s.State.ElapsedBioSeconds)).ToArray() };
        // Apply hemostasis immediately, before the next calendar settlement.
        return CommitNativeCare(s, WoundCare.BandageAll(frame, false));
    }
    internal bool ApplyConsumedBandage(Entity entity, long wound, bool antiseptic)
    {
        if (!OwnsWoundCare(entity) || !entity.Alive) return false;
        var s = SessionFor(entity);
        if (!s.Framework.Wounds.Any(w => w.EventSequence == wound && w.Healing < 1 && w.BandageQuality < 1)) return false;
        var treated = WoundCare.BandageAll(s.Framework, antiseptic);
        if (ReferenceEquals(treated, s.Framework)) return false;
        return CommitNativeCare(s, treated);
    }

    internal bool CommitConsumedRescue(Entity entity) => OwnsPhysiology(entity) && Save(SessionFor(entity));

    internal void BeginNativeCare(Entity actor)
    {
        if (OwnsPhysiology(actor)) CancelCompetingActions(SessionFor(actor));
    }

    private bool CommitNativeCare(Session s, FrameworkState next)
    {
        var before = s.Framework;
        s.Framework = next;
        if (!Save(s)) { s.Framework = before; return false; }
        // The commit receipt remains successful if a later notification fails.
        try { CancelCompetingActions(s); SendSnapshot(s); }
        catch (Exception ex) { api.Logger.Warning("[vsslssmod] Care committed; notification failed: {0}", ex.Message); }
        return true;
    }

    private ClinicalEffectSnapshot[] ClinicalProjection(Session s)
    {
        if (s.Deceased || s.Fault is not null || !IsSurvivalTestPlayer(s.Player)
            || !settings.Config.EnableClinicalEffects) return [];
        var effects = ClinicalEffects.Evaluate(s.Framework, s.State, s.Profile)
            .Select(e => new ClinicalEffectSnapshot { Key = e.Id, Severity = e.Severity, Beneficial = e.Beneficial,
                Description = L(s.Player, "effect-" + e.Id) + " · " + L(s.Player, "effect-help-" + e.Id) }).ToList();
        return effects.ToArray();
    }

    private void AnnounceClinicalEffects(Session s)
    {
        if (settings.Config.Mode != "survival" || !settings.Config.EnableClinicalEffects || s.Deceased
            || s.Fault is not null || s.Player.Entity?.Alive != true || !IsSurvivalTestPlayer(s.Player)
            || s.Environment is null || ServerSeconds - s.LastGoodSampleSeconds > 5)
        { s.ClinicalWarnings.Reset(); s.PendingInsectBites = 0; return; }
        if (paused) return;
        var warnings = s.ClinicalWarnings.Advance(ClinicalWarningPolicy.Evaluate(s.Framework, s.State, s.Profile, settings.Config.Exertion),
            Math.Clamp(s.LastDt, 0, 2), s.PendingInsectBites);
        s.PendingInsectBites = 0;
        if (warnings.Length == 0) return;
        string Title(ClinicalWarning warning) => warning.Key == "insect-bite"
            ? L(s.Player, "warning-insect-bite", warning.Count)
            : warning.Key == "exertion-stamina" ? L(s.Player, "warning-exertion-stamina", warning.Stage == 1 ? 20 : 5)
            : warning.Key == "exertion-lactate" ? L(s.Player, "warning-exertion-lactate", warning.Stage == 1 ? 80 : 95)
            : warning.Key == "exertion-exhausted" ? L(s.Player, "warning-exertion-exhausted")
            : warning.Key == "blood-toxicity"
                ? L(s.Player, "warning-blood-toxicity", Number(s.Framework.Blood.WasteMl / BloodCirculation.WasteCapacityMl(s.State, s.Profile) * 100, "0.0"),
                    Number(s.Framework.Blood.Toxicity * 100, "0.0"))
                : L(s.Player, warning.Stage > 1 ? "warning-stage" : "warning-state", L(s.Player, "effect-" + warning.Key), warning.Stage);
        var titles = warnings.Select(Title).ToArray();
        var screen = string.Join(" · ", titles.Take(3));
        if (titles.Length > 3) screen += " · " + L(s.Player, "warning-more", titles.Length - 3);
        s.Player.SendIngameError("vsslssmod-clinical", screen);
        // Detailed explanations stay in the character panel. One short message per batch
        // avoids repeatedly laying out long help paragraphs in the native chat HUD.
        s.Player.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
            screen, EnumChatType.Notification);
    }
}
