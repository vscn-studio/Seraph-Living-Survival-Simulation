using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seraph.Client;

/// <summary>Replicates the authoritative rest pose to the owner and other clients.</summary>
public sealed class ClosedEyeRestBehavior(Entity entity) : EntityBehavior(entity)
{
    private int mode;
    public override string PropertyName() => "vsslssmodclosedeyerest";

    public override void OnGameTick(float deltaTime)
    {
        var next = entity.Alive ? entity.WatchedAttributes.GetInt(ClosedEyePose.StateAttribute) : 0;
        if (next == mode) return;
        if (next != 0 && entity.AnimManager is null) return;
        if (mode != 0) entity.AnimManager?.StopAnimation(ClosedEyePose.Code);
        mode = next;
        if (mode is 1 or 2) entity.AnimManager?.StartAnimation(ClosedEyePose.Create(mode == 2));
    }

    public override void OnEntityDespawn(EntityDespawnData despawn) => Stop();

    private void Stop()
    {
        if (mode != 0) entity.AnimManager?.StopAnimation(ClosedEyePose.Code);
        mode = 0;
    }
}
