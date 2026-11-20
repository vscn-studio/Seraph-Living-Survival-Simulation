using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private sealed record PendingTreatment(ItemSlot Slot, ItemStack Stack, TreatmentDefinition Definition,
        double Started, double LastHold, long Generation, string Epoch, long Wound, int HurtCounter,
        double X, double Y, double Z);
    private readonly Dictionary<string, PendingTreatment> pendingTreatments = new();

    internal TreatmentUseResult BeginTreatment(IServerPlayer player, ItemSlot slot, TreatmentDefinition definition, TreatmentRoute route)
    {
        foreach (var uid in pendingTreatments.Where(kv => ServerSeconds - kv.Value.LastHold > 2).Select(kv => kv.Key).ToArray()) pendingTreatments.Remove(uid);
        var available = QueryTreatment(player, slot, definition, route);
        if (!available.Success) return available;
        var s = sessions[player.PlayerUID];
        var e = player.Entity;
        CancelCompetingActions(s);
        pendingTreatments[player.PlayerUID] = new(slot, slot.Itemstack!, definition, ServerSeconds, ServerSeconds,
            s.LifeGeneration, s.Epoch, available.Wound, e.WatchedAttributes.GetInt("onHurtCounter"), e.Pos.X, e.Pos.Y, e.Pos.Z);
        return available;
    }

    internal TreatmentUseResult QueryTreatment(IServerPlayer player, ItemSlot slot, TreatmentDefinition definition, TreatmentRoute route)
    {
        if (!ReadyForAction(player, out var s) || !settings.Config.EnableClinicalEffects || !settings.Config.EnableMedicalActions
            || !OwnsPhysiology(player.Entity)) return new(false, "treatment-unavailable");
        if (slot != player.InventoryManager.ActiveHotbarSlot || slot.Empty || route != definition.Route)
            return new(false, "treatment-route");
        if (MedicineSpoiled(slot))
            return new(false, "treatment-spoiled");
        var wound = definition.RequiresWound ? s.Framework.Wounds.Where(w => Treatments.MatchesWound(definition, w))
            .OrderByDescending(w => definition.Hemostasis > 0 ? WoundCare.Rate(w) : BodyFunctionResolver.Remaining(w))
            .ThenBy(w => w.EventSequence).Select(w => w.EventSequence).FirstOrDefault() : 0;
        if (definition.RequiresWound && (wound == 0 || !OwnsWoundCare(player.Entity))) return new(false, "treatment-no-wound");
        if (s.State.GutWaterL + definition.WaterL > s.Profile.MaximumGutWaterL
            || s.Framework.Nutrition.ContentsMassG(s.State.GutWaterL) + definition.WaterL * 1000 > DietaryMetabolism.StomachCapacityG) return new(false, "treatment-full");
        var e = player.Entity;
        if (e.MountedOn is not null || e.ServerControls.TriesToMove || e.Swimming
            || e.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") is > 0)
            return new(false, "treatment-interrupted");
        return new(true, "treatment-start", Treatments.RecentDoses(s.Framework, definition.Group, s.State.ElapsedBioSeconds), wound);
    }

    internal bool ContinueTreatment(IServerPlayer player, ItemSlot slot)
    {
        if (!pendingTreatments.TryGetValue(player.PlayerUID, out var p) || !TreatmentStillValid(player, slot, p))
        { pendingTreatments.Remove(player.PlayerUID); return false; }
        pendingTreatments[player.PlayerUID] = p with { LastHold = ServerSeconds };
        return true;
    }

    private bool TreatmentStillValid(IServerPlayer player, ItemSlot slot, PendingTreatment p)
    {
        if (!ReadyForAction(player, out var s) || !settings.Config.EnableClinicalEffects || !settings.Config.EnableMedicalActions
            || !OwnsPhysiology(player.Entity) || s.Epoch != p.Epoch || s.LifeGeneration != p.Generation
            || slot != p.Slot || slot != player.InventoryManager.ActiveHotbarSlot || !ReferenceEquals(slot.Itemstack, p.Stack)
            || slot.StackSize < 1 || ServerSeconds - p.LastHold > 1 || ServerSeconds < p.Started
            || MedicineSpoiled(slot)) return false;
        var e = player.Entity;
        return e.MountedOn is null && !e.ServerControls.TriesToMove && !e.Swimming
            && e.WatchedAttributes.GetInt("onHurtCounter") == p.HurtCounter
            && e.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") is not > 0
            && Math.Pow(e.Pos.X - p.X, 2) + Math.Pow(e.Pos.Y - p.Y, 2) + Math.Pow(e.Pos.Z - p.Z, 2) <= .25;
    }

    internal TreatmentUseResult CompleteTreatment(IServerPlayer player, ItemSlot slot)
    {
        if (!pendingTreatments.Remove(player.PlayerUID, out var p) || !TreatmentStillValid(player, slot, p)
            || ServerSeconds - p.Started < (p.Definition.Route == TreatmentRoute.Oral ? 3 : 5)) return new(false, "treatment-interrupted");
        var s = sessions[player.PlayerUID];
        var oldFrame = s.Framework; var oldBody = s.State;
        var receipt = s.Epoch + ":medicine:" + (oldFrame.EventSequence + 1);
        var stack = slot.Itemstack;
        var item = stack?.Collectible;
        if (stack is null || item is null) return new(false, "treatment-interrupted");
        var treated = Treatments.Apply(oldFrame, p.Definition, receipt, item.Code.ToString(),
            p.Definition.Route, oldBody.ElapsedBioSeconds, p.Wound);
        if (!treated.Accepted) return new(false, "treatment-invalid");
        var intake = s.Kernel.ApplyIntake(oldBody, new() { WaterL = p.Definition.WaterL });
        if (!intake.Accepted || Math.Abs(intake.AcceptedWaterL - p.Definition.WaterL) > 1e-8) return new(false, "treatment-full");
        var chemicals = stack.Attributes.GetDouble("vsshmod:chemicalDose");
        if (!double.IsFinite(chemicals) || chemicals is < 0 or > 1) return new(false, "treatment-invalid");
        s.State = intake.State;
        s.Framework = treated.State with { TotalIntakeWaterL = treated.State.TotalIntakeWaterL + intake.AcceptedWaterL,
            ChemicalBurden = Math.Clamp(treated.State.ChemicalBurden + chemicals, 0, 1) };
        var before = stack.Clone();
        try
        {
            slot.TakeOut(1); slot.MarkDirty();
            if (!Save(s)) throw new InvalidOperationException("Treatment checkpoint failed.");
        }
        catch
        {
            s.Framework = oldFrame; s.State = oldBody; slot.Itemstack = before; slot.MarkDirty();
            return new(false, "treatment-save-failed");
        }
        SendSnapshot(s);
        return new(true, "treatment-complete", Treatments.RecentDoses(s.Framework, p.Definition.Group, s.State.ElapsedBioSeconds), p.Wound);
    }

    internal void CancelTreatment(IServerPlayer player) => pendingTreatments.Remove(player.PlayerUID);

    private bool MedicineSpoiled(ItemSlot slot)
    {
        if (slot.Itemstack is not { } stack) return true;
        // Observe a clone: checking a held action must never transform its real stack into rot.
        var observed = stack.Clone();
        var observationSlot = new DummySlot(observed);
        var perish = stack.Collectible.UpdateAndGetTransitionState(api.World, observationSlot, EnumTransitionType.Perish);
        if (observationSlot.Empty || observationSlot.Itemstack.Collectible.Code != stack.Collectible.Code) return true;
        return perish is not null ? perish.TransitionedHours >= perish.FreshHours
            : stack.Attributes.GetDouble("vsshmod:expiresAt", double.MaxValue) <= api.World.Calendar.TotalHours;
    }

    internal void RecordPlantContact(IServerPlayer player, string source, double severity)
    {
        if (ReadyForAction(player, out var s) && settings.Config.EnableClinicalEffects && double.IsFinite(severity)
            && severity is > 0 and <= 1 && !string.IsNullOrWhiteSpace(source) && source.Length <= 96)
            s.Framework = Treatments.Complain(s.Framework, "dermatitis", source, s.State.ElapsedBioSeconds, 54000, severity);
    }
}
