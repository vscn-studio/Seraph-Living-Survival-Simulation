using Seraph.Network;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private void AdvanceSleepEyes(Session s, bool advancing, double dt)
    {
        var eligible = advancing && OwnsPhysiology(s.Player.Entity) && settings.Config.EnableClinicalEffects
            && settings.Config.EnableSymptomVisuals && ClosedEyeAwake(s.Player.Entity)
            && s.ClosedEyeRest == ClosedEyeRestMode.None
            && s.Framework.AlertnessReliefUntilBioSeconds <= s.State.ElapsedBioSeconds;
        s.SleepEyes.Advance(dt, eligible ? s.State.SleepPressure : 0);
        s.SleepEyesUpdatedAt = ServerSeconds;
    }

    private bool EyesClosed(Session s) => s.ClosedEyeRest != ClosedEyeRestMode.None
        || !ClosedEyeAwake(s.Player.Entity)
        // Hits may arrive between coordinator ticks: evaluate the current phase without advancing it twice.
        || s.SleepEyes.ClosureAfter(paused || !double.IsFinite(s.SleepEyesUpdatedAt) ? 0
            : ServerSeconds - s.SleepEyesUpdatedAt, s.State.SleepPressure) >= 1;

    private static SleepEyeSnapshot SleepEyeProjection(Session s)
    {
        var phase = s.SleepEyes.Capture();
        return new() { OpenTime = phase.OpenTime, Phase = phase.Phase, Hold = phase.Hold,
            Closing = phase.Closing, Sequence = phase.Sequence };
    }
}
