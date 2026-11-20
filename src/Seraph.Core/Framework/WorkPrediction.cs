namespace Seraph.Core.Framework;

public readonly record struct PredictedWork(long Sequence, ExertionKind Kind, double Seconds, bool Seated = false);

/// <summary>Matches prediction notices to work already accepted by native execution. Notices never charge work.</summary>
public sealed class WorkAcknowledgements
{
    private readonly Queue<(PredictedWork Work, double At)> pending = new();
    private readonly List<(ExertionKind Kind, double Seconds, double At)> credits = [];
    private long received;
    public long Acknowledged { get; private set; }
    public void Credit(ExertionKind kind, double seconds, double now)
    {
        Trim(now);
        if (seconds <= 0 || !double.IsFinite(seconds)) return;
        if (credits.Count > 0 && credits[^1].Kind == kind && now - credits[^1].At < .25)
        { var c = credits[^1]; credits[^1] = (kind, c.Seconds + seconds, c.At); }
        else { if (credits.Count >= 256) credits.RemoveAt(0); credits.Add((kind, seconds, now)); }
    }
    public void Notice(PredictedWork work, double now)
    {
        if (work.Sequence <= received || work.Sequence <= 0 || !Enum.IsDefined(work.Kind)
            || !double.IsFinite(work.Seconds) || work.Seconds <= 0 || work.Seconds > 1 || pending.Count >= 128) return;
        received = work.Sequence;
        pending.Enqueue((work, now));
    }
    public double[] Settle(double now)
    {
        Trim(now);
        while (pending.TryPeek(out var head))
        {
            var available = credits.Where(c => c.Kind == head.Work.Kind).Sum(c => c.Seconds);
            if (available + 1e-6 < head.Work.Seconds && now - head.At < 1) break;
            var remaining = Math.Min(available, head.Work.Seconds);
            for (var i = 0; i < credits.Count && remaining > 0; i++)
            {
                var c = credits[i];
                if (c.Kind != head.Work.Kind) continue;
                var used = Math.Min(c.Seconds, remaining);
                remaining -= used;
                credits[i] = (c.Kind, c.Seconds - used, c.At);
            }
            pending.Dequeue();
            Acknowledged = head.Work.Sequence; // also retires rejected or partially accepted reservations
        }
        var totals = new double[Enum.GetValues<ExertionKind>().Length];
        foreach (var c in credits) totals[(int)c.Kind] += c.Seconds;
        return totals;
    }
    private void Trim(double now) => credits.RemoveAll(c => now - c.At > 2 || c.Seconds <= 1e-9);
}

/// <summary>Bounded client reservations replayed over ordered authoritative reserves; unused credits avoid double charging.</summary>
public sealed class WorkPrediction
{
    private readonly List<PredictedWork> pending = [];
    private double[] credits = [];
    private long stateSequence, nextSequence;
    private double stamina = 1, lactate, capacity = 100, endurance = 1;
    private ExertionOptions options = new();
    private bool exhausted;
    private double jumpCooldown, jumpInterval = 3, unlockStamina = .20, unlockLactate = .6;
    private readonly Dictionary<long, double> predictedJumps = [];
    public double JumpCooldownRemaining
    {
        get { var remaining = jumpCooldown; foreach (var value in predictedJumps.Values) remaining = Math.Max(remaining, value); return remaining; }
    }
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        jumpCooldown = Math.Max(0, jumpCooldown - seconds);
        foreach (var key in predictedJumps.Keys.ToArray()) predictedJumps[key] = Math.Max(0, predictedJumps[key] - seconds);
    }
    private (double Stamina, double Lactate, bool Exhausted, string Reason)? cached;
    public bool Ready => stateSequence > 0;
    public double Capacity => capacity;
    public double UnlockStamina => unlockStamina;
    public double UnlockLactateRatio => unlockLactate;
    public bool Reconcile(long sequence, long acknowledged, double authoritativeStamina, double authoritativeLactate,
        double lactateCapacity, double laborEndurance, bool latched, double[] unusedCredits,
        double jumpCooldownRemaining = 0, double exhaustedJumpInterval = 3, double staminaUnlock = .20, double lactateUnlock = .6,
        ExertionOptions? exertionOptions = null)
    {
        if (sequence <= stateSequence || !double.IsFinite(authoritativeStamina) || !double.IsFinite(authoritativeLactate)
            || !double.IsFinite(lactateCapacity) || lactateCapacity <= 0 || !double.IsFinite(laborEndurance)
            || !double.IsFinite(jumpCooldownRemaining) || jumpCooldownRemaining < 0
            || !double.IsFinite(exhaustedJumpInterval) || exhaustedJumpInterval is < 1 or > 10
            || !double.IsFinite(staminaUnlock) || staminaUnlock is < .1 or > 1
            || !double.IsFinite(lactateUnlock) || lactateUnlock is <= 0 or >= 1
            || exertionOptions?.Validate() is not null) return false;
        stateSequence = sequence; nextSequence = Math.Max(nextSequence, acknowledged); cached = null;
        stamina = Math.Clamp(authoritativeStamina, 0, 1); lactate = Math.Clamp(authoritativeLactate, 0, lactateCapacity);
        capacity = lactateCapacity; endurance = laborEndurance; exhausted = latched;
        options = exertionOptions ?? new();
        pending.RemoveAll(p => p.Sequence <= acknowledged);
        foreach (var key in predictedJumps.Keys.Where(key => key <= acknowledged).ToArray()) predictedJumps.Remove(key);
        jumpCooldown = Math.Min(jumpCooldownRemaining, exhaustedJumpInterval);
        jumpInterval = exhaustedJumpInterval; unlockStamina = staminaUnlock; unlockLactate = lactateUnlock;
        credits = (double[])unusedCredits.Clone();
        return true;
    }
    public PredictedWork? Add(ExertionKind kind, double seconds, bool seated = false)
    {
        if (!Ready || pending.Count >= 128 || !double.IsFinite(seconds) || seconds <= 0 || seconds > 1) return null;
        var work = new PredictedWork(++nextSequence, kind, seconds, seated);
        if (kind == ExertionKind.Jump) predictedJumps[work.Sequence] = jumpInterval;
        pending.Add(work); cached = null;
        return work;
    }
    public (double Stamina, double Lactate, bool Exhausted, string Reason) Reserves()
    {
        if (cached is { } result) return result;
        var s = stamina; var l = lactate; var remainingCredits = (double[])credits.Clone();
        foreach (var work in pending)
        {
            var seconds = work.Seconds;
            var index = (int)work.Kind;
            if (index < remainingCredits.Length)
            {
                var paid = Math.Min(seconds, Math.Max(0, remainingCredits[index]));
                remainingCredits[index] -= paid; seconds -= paid;
            }
            var multiplier = Exertion.WorkMultiplier(work.Kind, work.Seated);
            var fraction = RunningPace.Fraction(s, l / capacity, exhausted || s <= 0 || l >= capacity, options);
            s = Math.Clamp(s + Exertion.StaminaDelta(work.Kind, seconds * multiplier, endurance, options), 0, 1);
            l = Math.Min(capacity, l + BloodCirculation.LactateRate(work.Kind, endurance, fraction) * seconds * multiplier);
        }
        var blocked = exhausted || s <= 0;
        var projected = (s, l, blocked, !blocked ? "" : s < unlockStamina ? "stamina" : "release");
        cached = projected; return projected;
    }
}
