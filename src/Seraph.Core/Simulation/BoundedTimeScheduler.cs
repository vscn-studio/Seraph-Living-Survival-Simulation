using Seraph.Core.State;

namespace Seraph.Core.Simulation;

public sealed record SchedulerOptions
{
    public const double MinimumPhysiologySpeed = 0.1;
    public const double MaximumPhysiologySpeed = 20;

    public double PhysiologySpeed { get; init; } = 1;
    public double MaximumServerSecondsPerRound { get; init; } = 2;
    public double MaximumPendingServerSeconds { get; init; } = 5;
    public double MaximumSubstepBioSeconds { get; init; } = 0.25;
    public int MaximumSubstepsPerRound { get; init; } = 32;

    public string? Validate() =>
        !BodyProfile.In(PhysiologySpeed, MinimumPhysiologySpeed, MaximumPhysiologySpeed)
        || !BodyProfile.In(MaximumServerSecondsPerRound, 0.001, 2)
        || !BodyProfile.In(MaximumPendingServerSeconds, MaximumServerSecondsPerRound, 5)
        || !BodyProfile.In(MaximumSubstepBioSeconds, 0.001, 0.25)
        || MaximumSubstepsPerRound is < 1 or > 32 ? "Invalid scheduler bounds or physiology speed." : null;
}

public sealed record TimeBudgetPlan(
    bool Accepted, int SubstepCount, double SubstepBioSeconds, double AcceptedBioSeconds,
    double DroppedServerSeconds, double PendingServerSeconds, string? Error);

/// <summary>
/// Per-player bounded accumulator. Caller supplies validated active server elapsed time, never calendar elapsed time.
/// Pause discards pending time. Instance must survive callbacks; it is not thread-safe.
/// </summary>
public sealed class BoundedTimeScheduler
{
    private readonly SchedulerOptions options;
    private double pendingServerSeconds;

    public BoundedTimeScheduler(SchedulerOptions? options = null)
    {
        this.options = options ?? new SchedulerOptions();
        var error = this.options.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(options));
    }

    public TimeBudgetPlan Schedule(double serverDeltaSeconds, bool paused = false)
    {
        if (!double.IsFinite(serverDeltaSeconds) || serverDeltaSeconds < 0)
            return new(false, 0, 0, 0, 0, pendingServerSeconds, "Invalid server elapsed time.");
        if (paused)
        {
            var discarded = pendingServerSeconds + serverDeltaSeconds;
            pendingServerSeconds = 0;
            return new(true, 0, 0, 0, discarded, 0, null);
        }

        // Avoid overflowing an accumulator even for a finite but enormous callback delta.
        var room = options.MaximumPendingServerSeconds - pendingServerSeconds;
        var retained = Math.Min(serverDeltaSeconds, room);
        var dropped = serverDeltaSeconds - retained;
        pendingServerSeconds += retained;
        var acceptedServerSeconds = Math.Min(pendingServerSeconds, options.MaximumServerSecondsPerRound);
        acceptedServerSeconds = Math.Min(acceptedServerSeconds,
            options.MaximumSubstepsPerRound * options.MaximumSubstepBioSeconds / options.PhysiologySpeed);
        var acceptedBioSeconds = acceptedServerSeconds * options.PhysiologySpeed;
        if (acceptedBioSeconds <= 0)
            return new(true, 0, 0, 0, dropped, pendingServerSeconds, null);
        var count = Math.Min(options.MaximumSubstepsPerRound,
            (int)Math.Ceiling(acceptedBioSeconds / options.MaximumSubstepBioSeconds));
        pendingServerSeconds = Math.Max(0, pendingServerSeconds - acceptedServerSeconds);
        return new(true, count, acceptedBioSeconds / count, acceptedBioSeconds,
            dropped, pendingServerSeconds, null);
    }
}
