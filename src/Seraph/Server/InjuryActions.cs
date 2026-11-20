using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    internal FunctionSnapshot? ActionFunctions(Entity entity)
    {
        if (entity is not EntityPlayer playerEntity || playerEntity.Player is not IServerPlayer player || !ReadyForAction(player, out var s)
            || s.Environment is null || ServerSeconds - s.LastGoodSampleSeconds > 5
            || player.WorldData.CurrentGameMode != EnumGameMode.Survival) return null;
        s.SprintExhausted = Exertion.Exhausted(s.Framework.Stamina, s.Framework.Blood.LactateG,
            BloodCirculation.LactateCapacityG(s.State, s.Profile), s.SprintExhausted, true, options: settings.Config.Exertion);
        var f = EffectiveFunctions(s);
        var inputs = (f, playerEntity.MountedOn is null && barrier.Allows(SideEffectKind.Action, Capability.Movement),
            barrier.Allows(SideEffectKind.Action, Capability.Manipulation), s.SprintExhausted, ExhaustionReason(s), OwnsOxygen(entity), JumpAllowed(s),
            s.RunningPace.Gain(settings.Config.Exertion, s.Framework.Stamina,
                s.Framework.Blood.LactateG / BloodCirculation.LactateCapacityG(s.State, s.Profile), s.SprintExhausted));
        if (s.ActionFunctionValues is { } cached && s.ActionFunctionInputs == inputs) return cached;
        s.ActionFunctionInputs = inputs;
        return s.ActionFunctionValues = new() {
            Movement = inputs.Item2, Manipulation = inputs.Item3,
            Jump = f.Jump, Sprint = f.Sprint, Bow = f.Bow, Mining = f.Mining, HandCapacity = f.Manipulation,
            LaborLoad = f.LaborLoad, SprintLoad = f.SprintLoad, Fatigue = s.Framework.Fatigue, SleepPressure = s.State.SleepPressure,
            CanJump = inputs.Item7, BodyCanJump = f.CanJump, CanSprint = f.CanSprint, PaceGain = inputs.Item8, MeleeAttackSpeed = f.MeleeAttackSpeed,
            CanDrawBow = f.CanDrawBow && !s.SprintExhausted, CanUseActiveHand = f.CanUseActiveHand && !s.SprintExhausted,
            ExhaustionReason = inputs.Item5, OxygenReplacement = inputs.Item6
        };
    }

    private string ExhaustionReason(Session s) => !s.SprintExhausted ? ""
        : s.Framework.Stamina < settings.Config.Exertion.UnlockStamina ? "stamina" : "release";

    internal void DeniedLabor(Entity entity)
    {
        if (OwnsPhysiology(entity)) SessionFor(entity).DeniedReason = ExhaustionReason(SessionFor(entity));
    }

    internal void DeniedJump(Entity entity)
    {
        if (OwnsPhysiology(entity)) SessionFor(entity).DeniedReason = "jump-cooldown";
    }

    private bool ClosedEyeTurnEnabled(Session s) => settings.Config.EnableDrowsyMovement
        && settings.Config.EnableSymptomVisuals && settings.Config.EnableClinicalEffects && s.Fault is null && !s.Deceased
        && s.Environment is not null && ServerSeconds - s.LastGoodSampleSeconds <= 5
        && s.Player.WorldData.CurrentGameMode == EnumGameMode.Survival
        && ClosedEyeAwake(s.Player.Entity)
        && barrier.Allows(SideEffectKind.Action, Capability.Movement);

    // Missing vanilla tiredness metadata is not evidence of sleeping. Other mods may
    // remove that behavior while the independent sleep-pressure model still runs.
    internal static bool ClosedEyeAwake(EntityPlayer entity) =>
        entity.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") is not > 0;

    private void CheckInjuryBlockBreak(IServerPlayer player, BlockSelection selection, ref float dropQuantityMultiplier, ref EnumHandling handling)
    {
        // Native resistance is integrated by the client. This server event also rejects a severely disabled
        // active hand before block removal, drops or durability, including empty-hand mining packets.
        if (ActionFunctions(player.Entity) is { Manipulation: true, CanUseActiveHand: false })
        { handling = EnumHandling.PreventDefault; DeniedLabor(player.Entity); }
    }
}
