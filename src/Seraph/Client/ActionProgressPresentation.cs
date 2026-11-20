using Seraph.Network;

namespace Seraph.Client;

/// <summary>Presentation only. It cannot finish, reward or cancel an authoritative operation.</summary>
internal sealed class ActionProgressPresentation
{
    private string epoch = "", actionKey = "";
    private long pressureActionId;
    private long sequence = -1, pressureSequence = -1;
    private double elapsed, pressureElapsed;
    internal bool Visible { get; private set; }
    internal bool Activity { get; private set; }
    internal double Fraction { get; private set; }
    internal double Seconds { get; private set; }
    internal double AnimationTime { get; private set; }
    internal string Action { get; private set; } = "";

    internal void Update(ServerSnapshot? state, PressureStatus? pressure, double dt, bool fresh, bool paused)
    {
        Visible = Activity = false; Fraction = 0; Action = "";
        if (!fresh || state is null) { Reset(); return; }
        var step = paused || !double.IsFinite(dt) ? 0 : Math.Clamp(dt, 0, .25);
        AnimationTime += step;
        if (epoch != state.SessionEpoch) { epoch = state.SessionEpoch; sequence = pressureSequence = -1; }
        if (pressure is { Active: true } p && double.IsFinite(p.ElapsedSeconds))
        {
            pressureElapsed = p.Sequence != pressureSequence || p.ActionId != pressureActionId ? Math.Max(0, p.ElapsedSeconds) : pressureElapsed + step;
            pressureSequence = p.Sequence; pressureActionId = p.ActionId;
            Activity = Visible = true; Action = "pressure"; Seconds = pressureElapsed;
        }
        var action = state.ActionProgress;
        if (action is not { DurationSeconds: > 0 } || !double.IsFinite(action.DurationSeconds)
            || !double.IsFinite(action.ElapsedSeconds)) { sequence = -1; actionKey = ""; return; }
        elapsed = state.Sequence != sequence || actionKey != action.Action ? Math.Max(0, action.ElapsedSeconds) : elapsed + step;
        sequence = state.Sequence; actionKey = action.Action;
        if (Activity) return;
        // A pressure snapshot alone must never be interpreted as a finite completion timer.
        if (action.Action == "pressure") return;
        Visible = true; Action = action.Action;
        Seconds = Math.Min(elapsed, action.DurationSeconds * .98);
        Fraction = Math.Clamp(Seconds / action.DurationSeconds, 0, .98);
    }
    internal void Reset()
    { epoch = actionKey = ""; pressureActionId = 0; sequence = pressureSequence = -1; elapsed = pressureElapsed = AnimationTime = Seconds = Fraction = 0; Visible = Activity = false; Action = ""; }
}
