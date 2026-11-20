using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class BalanceChecks
{
    public static void PublishedFlows()
    {
        var work = new SimulationDiagnostics { AcceptedBioSeconds = 3, ActivityEnergyJ = 3 * 450 * 4184 / 3600d,
            BasalEnergyJ = 3 * 70 * 4184 / 3600d, AbsorbedWaterL = .001, GlycogenConsumedKJ = 2 };
        var rest = new SimulationDiagnostics { AcceptedBioSeconds = 9, BasalEnergyJ = 9 * 70 * 4184 / 3600d,
            InsensibleWaterL = .00025, RespiratoryWaterL = .0001 };
        var ledger = new PhysiologyFlowLedger().Add(work, 1, .4, .2, .8, .2).Add(rest, .5, .3, .1, .5);
        Near(ledger.KcalPerHour(ledger.ActivityJ), 112.5);
        Near(ledger.KcalPerHour(ledger.BasalJ), 70);
        Near(ledger.RecoveryJ, 1500);
        Near(ledger.GlycogenKJ, 3.3); Near(ledger.FatKJ, .2);
        Near(ledger.Hourly(ledger.AbsorbedWaterL * 1000), 300);
        Near(ledger.Hourly(ledger.LostWaterL * 1000), 105);
        Near(ledger.Hourly(ledger.WasteProducedMl), 210);
        Near(ledger.Hourly(ledger.WasteClearedMl), 90);
    }

    public static void WorkRestPartitions()
    {
        // Work - brief rest - work lies inside one server sampling window.
        // Lactate clearance must only see the real rest segment, with highest overlapping fuel tier.
        (double Lactate, double Energy) Settle(bool sampled)
        {
            var work = new WorkIntervals(); var energy = new ActionEnergyBudget();
            work.Add(0, 1); work.Add(1.2, 2);
            energy.Record(450, 0, 1); energy.Record(160, .4, 1.6); energy.Record(450, 1.2, 2);
            var edges = sampled ? Enumerable.Range(0, 21).Select(n => n / 10d).ToArray()
                : work.Boundaries(0, 2).Concat(energy.Boundaries(0, 2)).Append(0).Append(2).Distinct().Order().ToArray();
            var p = BodyProfile.Default; var b = BodyState.Create(p);
            var frame = new FrameworkState { Blood = new() { LactateG = 50 } }; double paid = 0;
            for (var i = 1; i < edges.Length; i++)
            {
                var seconds = edges[i] - edges[i - 1];
                var resting = work.RestSeconds(edges[i - 1], edges[i]) / seconds;
                paid += energy.Consume(edges[i - 1], edges[i]) * seconds;
                var count = (int)Math.Ceiling(seconds * 30 / 8);
                for (var n = 0; n < count; n++)
                    frame = BloodCirculation.Settle(frame, b, b, p, new(), 0, seconds * 30 / count,
                        BowelSettlement.CapacityG, true, preparedRenalSupport: 0, restFraction: resting).State;
            }
            return (frame.Blood.LactateG, paid);
        }
        var whole = Settle(false); var split = Settle(true);
        Near(whole.Lactate, 50 - .2 * 30 * 400 / 3600);
        Near(whole.Energy, 450 * 1.8 + 160 * .2);
        Near(whole.Lactate, split.Lactate); Near(whole.Energy, split.Energy);
        var clock = new RecoveryClock(); clock.RecordWork(1);
        Near(clock.Seconds(1, 1.2), 0); clock.RecordWork(2);
        Near(clock.Seconds(2, 4), 0); Near(clock.Seconds(4, 5), 1);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var intervals = new WorkIntervals(); var activity = new ActionEnergyBudget();
        for (var i = 0; i < 60000; i++)
        { intervals.Add(i / 100d, (i + 1) / 100d); activity.Record(450, i / 100d, (i + 1) / 100d); }
        Near(intervals.RestSeconds(0, 600), 0); Near(activity.Consume(0, 600), 450);
        Console.WriteLine($"PERF 60000 overlapping labor receipts and settlement: {timer.Elapsed.TotalMilliseconds:0.00} ms (synthetic, no game rendering/network).");
    }

    private static void Near(double a, double b, double tolerance = 1e-8)
    { if (Math.Abs(a - b) > tolerance) throw new Exception($"Balance mismatch: {a} vs {b}"); }
}
