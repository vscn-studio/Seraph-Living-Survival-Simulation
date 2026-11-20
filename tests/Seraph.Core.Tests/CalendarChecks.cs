using Seraph.Core.Simulation;
using Seraph.Core.State;
using Seraph.Core.Framework;

internal static class CalendarChecks
{
    public static void ActionEnergy()
    {
        var actions = new ActionEnergyBudget();
        actions.Record(450, 0, 1); actions.Record(160, 0, 1); actions.Record(450, .25, .75);
        if (actions.Consume(0, 1, 160) != 450) throw new Exception("Overlapping work duplicated activity energy.");
        if (actions.Consume(1, 2) != 0) throw new Exception("Cancelled work leaked into rest.");
        var whole = new ActionEnergyBudget(); var split = new ActionEnergyBudget();
        foreach (var ledger in new[] { whole, split }) { ledger.Record(450, .2, .7); ledger.Record(160, .5, 1.2); }
        var expected = whole.Consume(0, 2, 160) * 2;
        double paid = 0;
        for (var i = 0; i < 8; i++) paid += split.Consume(i / 4d, (i + 1) / 4d, 160) / 4;
        if (Math.Abs(paid - expected) > 1e-9 || Math.Abs(expected - (450 * .5 + 160 * 1.5)) > 1e-9)
            throw new Exception("Activity tier changed with interval partition or overlap.");
        foreach (var speed in new[] { 1d, 30d, 120d, 1200d })
        {
            var ledger = new ActionEnergyBudget(); ledger.Record(450 * 4184 / 3600d, 0, 1);
            var power = ledger.Consume(0, 1);
            var calendarEnergy = power * speed;
            if (Math.Abs(calendarEnergy / speed * 3600 / 4184 - 450) > 1e-9)
                throw new Exception("Accelerated calendar diluted activity tier.");
        }
    }
    public static void Debt()
    {
        var clock = new CalendarTimeScheduler();
        double settled = clock.Schedule(1000).AcceptedBioSeconds;
        var debt = clock.PendingSeconds;
        clock.Schedule(5000, paused: true);
        if (clock.PendingSeconds != debt) throw new Exception("Pause discarded earned time.");
        while (clock.PendingSeconds > 0) settled += clock.Schedule(0).AcceptedBioSeconds;
        if (Math.Abs(settled - 1000) > 1e-9) throw new Exception("Calendar debt was lost.");
    }

    public static void CropPartition()
    {
        var region = new Seraph.Core.Framework.RegionalEnvironment { SoilPathogens = .2, SurfacePathogens = .1,
            SoilAgents = new Dictionary<string, double> { ["ecoli"] = .3 } };
        var whole = Seraph.Core.Framework.CropSurfaceContamination.Advance(Seraph.Core.Framework.PathogenLoads.Empty, region, 86400);
        var split = Seraph.Core.Framework.PathogenLoads.Empty;
        for (var i = 0; i < 288; i++) split = Seraph.Core.Framework.CropSurfaceContamination.Advance(split, region, 300);
        if (whole.Count != 2 || whole.Keys.Any(k => Math.Abs(whole[k] - split[k]) > 1e-10)) throw new Exception("Crop exposure changed with partition or lost generic pollution.");
        var clean = Seraph.Core.Framework.CropSurfaceContamination.Advance(whole, new(), 86400);
        if (whole.Keys.Any(k => Math.Abs(clean[k] - whole[k] / 2) > 1e-12)) throw new Exception("Crop half-life failed.");
    }

    public static void Rates()
    {
        void Near(double a, double b) { if (Math.Abs(a-b) > 1e-9) throw new Exception($"Rate mismatch: {a} / {b}"); }
        var p = BodyProfile.Default;
        var b = BodyState.Create(p);
        var f = new FrameworkState { Fatigue = .6 };
        var kernel = new FrameworkKernel();
        var effort = new ActivityContext(160 * 4184d / 3600, false, false, false, false, 1, true);
        Near(.6 + 160d / 3500 * 8 / 3600, kernel.Advance(f,b,p,new(),effort,8).State.Fatigue);
        Near(.6, kernel.Advance(f,b,p,new(),effort with { PaidActivityEnergyJ = 0 },8).State.Fatigue);
        Near(.6 + 1000d / (3500 * 4184), kernel.Advance(f,b,p,new(),effort with { PaidActivityEnergyJ = 1000 },8).State.Fatigue);
        Near(.6 - 8d / 28800, kernel.Advance(f,b,p,new(),effort with { Sleeping = true },8).State.Fatigue);
        Near(.6 - 8d * .385 / 28800, kernel.Advance(f,b,p,new(),effort with { Resting = true },8).State.Fatigue);
        var sim = new SimulationKernel(p, new() { EnableDryHeatExchange = false, EnableSweating = false,
            EnableEvaporation = false, EnableRespiration = false, MaximumShiveringPowerW = 0 });
        Near(.3 + 8d / 90000,sim.Advance(b,new(),new(),8).State.SleepPressure);
        Near(.8 - 8d / 28800,sim.Advance(b with { SleepPressure=.8 },new(),new() { IsSleeping=true },8).State.SleepPressure);
        Near(.8 - 8d * .385 / 28800,sim.Advance(b with { SleepPressure=.8 },new(),new() { IsResting=true },8).State.SleepPressure);
        Near(.3,sim.Advance(b,new(),new() { IsResting=true },8).State.SleepPressure);
        var meal = b with { GutEnergyKJ = 4000 };
        var carbs = new NutritionState { GutMassG = 100 };
        carbs.Pending[0] = 100;
        var proteins = new NutritionState { GutMassG = 100 };
        proteins.Pending[1] = 100;
        var starch = sim.Advance(meal, new(), new() { Nutrition = carbs }, 8);
        var protein = sim.Advance(meal, new(), new() { Nutrition = proteins }, 8);
        Near(4000 * (1 - Math.Pow(2, -8d / 5400)), starch.Diagnostics.AbsorbedEnergyKJ);
        Near(4000 * (1 - Math.Pow(2, -8d / 7200)), protein.Diagnostics.RepairMaterialEnergyKJ);

    }

    public static void WeeklySleep()
    {
        var profile = BodyProfile.Default with { BasalMetabolicPowerW = 0 };
        var kernel = new SimulationKernel(profile, new() { EnableDryHeatExchange = false, EnableRespiration = false,
            EnableEvaporation = false, EnableSweating = false, EnableInsensibleWaterLoss = false,
            EnableFecalWaterLoss = false, MaximumShiveringPowerW = 0 });
        var state = BodyState.Create(profile);
        var framework = new FrameworkKernel();
        var frame = FrameworkState.Create("seven-day-rest");
        // Isolate pacing with steady food/water/temperature support. This is not an end-to-end feeding simulation.
        void Step(bool sleeping)
        {
            state = kernel.Advance(state, new(), new() { IsSleeping = sleeping, SleepQuality = .9 }, 8).State;
            var result = framework.Advance(frame, state, profile, new(), new(sleeping ? 0 : 120,
                sleeping, false, false, false, 1, true) { SleepQuality = .9 }, 8);
            if (!result.Accepted) throw new Exception(result.Error);
            frame = result.State;
        }
        for (var day = 0; day < 7; day++)
        {
            for (var i = 0; i < 17 * 3600 / 8; i++) Step(false);
            if (frame.Fatigue >= .6 || RestFeedback.FatigueMovementPenalty(frame.Fatigue) != 0
                || BodyFunctionResolver.Evaluate(frame, state, profile).Jump < .9)
                throw new Exception($"Ordinary exploration required extra rest on day {day + 1}.");
            var tired = frame.Fatigue;
            var hours = 0d;
            while (state.SleepPressure > .12 && hours < 12)
            {
                Step(true);
                hours += 8d / 3600;
            }
            if (hours < 6 || hours > 8) throw new Exception($"Sleep outside daily target: {hours:0.00} h");
            if (frame.Fatigue >= tired * .2) throw new Exception("Daily sleep did not clear ordinary fatigue.");
        }
    }
}
