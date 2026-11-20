using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private TextCommandResult ToggleClosedEyeRest(Session session)
    {
        var entity = session.Player.Entity;
        if (session.ClosedEyeRest != ClosedEyeRestMode.None)
        {
            ClearClosedEyeRest(session);
            return TextCommandResult.Success(L(session.Player, "closed-eye-rest-ended"));
        }
        if (!ReadyForAction(session.Player, out _)
            || !entity.Alive || entity.MountedOn is not null || entity.Swimming
            || entity.ServerControls.TriesToMove || !entity.OnGround
            || entity.ServerControls.IsFlying || entity.ServerControls.IsClimbing
            || entity.ServerControls.DetachedMode || entity.ServerControls.NoClip)
            return TextCommandResult.Error(L(session.Player, "closed-eye-rest-unavailable"));

        if (entity.GetBehavior<EntityBehaviorTiredness>()?.IsSleeping == true)
            return TextCommandResult.Error(L(session.Player, "closed-eye-rest-unavailable"));

        CancelCompetingActions(session);
        session.ClosedEyeRest = entity.ServerControls.FloorSitting
            ? ClosedEyeRestMode.Seated : ClosedEyeRestMode.Reclined;
        // Animation metadata sent only by server is unknown to clients. Sync the
        // state, then let every client's player behavior apply a local native pose.
        entity.WatchedAttributes.SetInt(ClosedEyePose.StateAttribute, (int)session.ClosedEyeRest);
        entity.WatchedAttributes.MarkPathDirty(ClosedEyePose.StateAttribute);
        session.ClosedEyePoseEntity = entity;
        return TextCommandResult.Success(L(session.Player, "closed-eye-rest-started"));
    }

    private void ValidateClosedEyeRest(Session session)
    {
        if (session.ClosedEyeRest == ClosedEyeRestMode.None) return;
        var entity = session.Player.Entity;
        var tired = entity.GetBehavior<EntityBehaviorTiredness>();
        if (entity.ServerControls.LeftMouseDown || entity.ServerControls.RightMouseDown
            || !entity.Alive || entity.MountedOn is not null || entity.Swimming || entity.ServerControls.TriesToMove
            || !entity.OnGround || entity.ServerControls.IsFlying || entity.ServerControls.IsClimbing
            || entity.ServerControls.DetachedMode || entity.ServerControls.NoClip || tired?.IsSleeping == true)
            ClearClosedEyeRest(session);
    }

    private static void ClearClosedEyeRest(Session session)
    {
        var entity = session.ClosedEyePoseEntity ?? session.Player.Entity;
        entity?.WatchedAttributes.SetInt(ClosedEyePose.StateAttribute, 0);
        entity?.WatchedAttributes.MarkPathDirty(ClosedEyePose.StateAttribute);
        session.ClosedEyePoseEntity = null;
        session.ClosedEyeRest = ClosedEyeRestMode.None;
    }
}
