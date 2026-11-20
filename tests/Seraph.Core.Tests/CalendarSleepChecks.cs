using Seraph.Core.Simulation;

internal static class CalendarSleepChecks
{
    internal static void Boundaries()
    {
        CalendarSleepTimeline Timeline()
        {
            var timeline = new CalendarSleepTimeline();
            timeline.Reset(false, 1);
            timeline.Observe(1, true, .8);
            timeline.Observe(1, true, .8); // repeated native setter
            timeline.Observe(3, false, 1);
            timeline.Observe(4, true, .5);
            timeline.Observe(6, false, 1);
            return timeline;
        }

        var whole = Timeline().Consume(0, 8);
        var split = Timeline();
        var parts = new[] { (0d, 1d), (1d, 2d), (2d, 4d), (4d, 6d), (6d, 8d) }
            .SelectMany(p => split.Consume(p.Item1, p.Item2)).ToArray();
        foreach (var quality in new[] { 1d, .8, .5 })
        {
            var expected = whole.Where(p => p.BedQuality == quality).Sum(p => p.Hours);
            var actual = parts.Where(p => p.BedQuality == quality).Sum(p => p.Hours);
            Require(Math.Abs(expected - actual) < 1e-12, "Partition changed the sleep duration or bed quality.");
        }
        Require(whole.Sum(p => p.Hours) == 8 && whole.Where(p => p.Sleeping).Sum(p => p.Hours) == 4,
            "Sleep boundaries omitted or duplicated calendar time.");
        Require(split.Consume(8, 8).Count == 0, "Zero elapsed time created a sleep period.");

        var reset = Timeline();
        reset.Reset(false, 1); // pause/resume, time command or respawn
        Require(reset.Consume(0, 8) is [{ Hours: 8, Sleeping: false }], "Reset retained old sleep transitions.");
        reset.Observe(9, true, .8);
        reset.Consume(8, 10); // frozen physiology still consumes its observation history
        Require(reset.Consume(10, 11) is [{ Hours: 1, Sleeping: true, BedQuality: .8 }],
            "Consumed transitions leaked into a later interval.");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
