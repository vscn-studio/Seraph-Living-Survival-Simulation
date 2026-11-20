using Seraph.Network;
using Vintagestory.API.Client;

namespace Seraph.Client;

/// <summary>One native yaw change on entering full closure. Never owns controls or movement.</summary>
internal sealed class ClosedEyeOrientation(ICoreClientAPI api)
{
    // Called only on the eye cycle's rising edge; a rejected edge is consumed, never queued.
    internal bool TryTurn(ServerSnapshot state)
    {
        var player = api.World.Player?.Entity;
        if (!state.SymptomVisuals || !state.DrowsyMovement || state.IsSleeping || state.IsClosedEyeResting || state.Functions is not { Movement: true }
            || player is not { Alive: true, OnGround: true, Swimming: false, MountedOn: null }
            || api.IsGamePaused || !api.Input.MouseGrabbed || SymptomRegions.HasMenu(api)
            || api.Gui.OpenedGuis.Any(g => g.IsOpened() && g.PrefersUngrabbedMouse)
            || player.Controls.IsFlying || player.Controls.IsClimbing || player.Controls.FloorSitting
            || player.Controls.DetachedMode || player.Controls.NoClip) return false;
        var yaw = api.Input.MouseYaw;
        if (!float.IsFinite(yaw)) return false;
        var delta = (Math.PI / 3 + Random.Shared.NextDouble() * (2 * Math.PI / 3))
            * (Random.Shared.Next(2) == 0 ? -1 : 1);
        var next = (float)((yaw + delta + 2 * Math.PI) % (2 * Math.PI));
        if (next < 0) next += (float)(2 * Math.PI);
        api.Input.MouseYaw = next;
        player.Pos.Yaw = next;
        return true;
    }
}
