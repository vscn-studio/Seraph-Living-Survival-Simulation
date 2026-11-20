using Seraph.Core.Framework;

internal static class ExertionChecks
{
    public static void NutritionCapacity()
    {
        var empty = new NutritionState();
        var filled = NutritionState.Starting();
        foreach (var id in new[] { "carbohydrate", "protein", "fat", "electrolytes" })
        {
            var index = Nutrients.IndexOf(id);
            filled.Reserve[index] = Nutrients.All[index].Maximum;
        }
        var baseline = Exertion.StaminaCapacity(1, empty);
        var boosted = Exertion.StaminaCapacity(1, filled);
        if (Math.Abs(baseline - 1.5) > 1e-12 || Math.Abs(boosted - 1.8) > 1e-12
            || Exertion.StaminaCapacity(.5, filled) != boosted * .5)
            throw new Exception("Nutrition stamina bonus must be positive, bounded and preserve physical limits.");
        var prediction = new WorkPrediction();
        prediction.Reconcile(1, 0, 1, 0, 100, boosted, false, []);
        prediction.Add(ExertionKind.Mining, 1);
        var actual = Exertion.Apply(1, 1, ExertionKind.Mining, 1, true, false, false, capacity: boosted);
        if (Math.Abs(prediction.Reserves().Stamina - actual.Stamina) > 1e-12)
            throw new Exception("Predicted and actual boosted stamina diverged.");
        var rest = Exertion.Apply(0, 1, ExertionKind.Standing, 1, true, true, false,
            capacity: boosted * .5, recoveryCapacity: .5);
        if (Math.Abs(rest.Stamina - .012) > 1e-12) throw new Exception("Stamina bonus changed injured recovery.");
    }
    public static void Prediction()
    {
        static void Check(bool ok) { if (!ok) throw new Exception("Work prediction contract failed."); }
        var prediction = new WorkPrediction();
        Check(prediction.Add(ExertionKind.Attack, 1) is null);
        prediction.Reconcile(1, 0, .07, 97, 100, 1, false, []);
        var work = prediction.Add(ExertionKind.Attack, 1)!.Value;
        Check(prediction.Reserves() is { Exhausted: false, Lactate: 97 });
        var server = new WorkAcknowledgements();
        server.Notice(work, 0); // prediction arrives before the native operation
        Check(server.Settle(0).Sum() == 0 && server.Acknowledged == 0);
        server.Credit(ExertionKind.Attack, 1, .1);
        var credits = server.Settle(.1);
        Check(server.Acknowledged == work.Sequence && credits.Sum() == 0);
        prediction.Reconcile(2, server.Acknowledged, .0075, 100, 100, 1, true, credits);
        Check(Math.Abs(prediction.Reserves().Stamina - .0075) < 1e-12);
        Check(!prediction.Reconcile(1, 0, 1, 0, 100, 1, false, []));
        Check(prediction.Reserves().Exhausted); // stale packet cannot unlock
        prediction.Reconcile(3, work.Sequence, .3, 99, 100, 1, false, []);
        Check(!prediction.Reserves().Exhausted);
        work = prediction.Add(ExertionKind.Crafting, 1)!.Value;
        server.Notice(work, .2); // rejected native transaction; no credit
        Check(server.Settle(1).Sum() == 0 && server.Acknowledged < work.Sequence);
        credits = server.Settle(1.21);
        prediction.Reconcile(4, server.Acknowledged, .3, 99, 100, 1, false, credits);
        Check(prediction.Reserves().Stamina == .3); // rejected reservation released
        work = prediction.Add(ExertionKind.Mining, .2)!.Value;
        server.Credit(ExertionKind.Mining, .2, 2); // native execution precedes notice
        credits = server.Settle(2);
        prediction.Reconcile(5, server.Acknowledged, .2964, 99.2, 100, 1, false, credits);
        Check(Math.Abs(prediction.Reserves().Stamina - .2964) < 1e-12); // no fee twice
        server.Notice(work, 2.1); server.Notice(work, 2.1); // retransmission is free
        Check(server.Settle(2.1).Sum() == 0 && server.Acknowledged == work.Sequence);
        prediction.Reconcile(6, server.Acknowledged, .2964, 99.2, 100, 1, false, []);
        Check(Math.Abs(prediction.Reserves().Stamina - .2964) < 1e-12);
        var freshLife = new WorkPrediction();
        freshLife.Reconcile(1, 0, 1, 0, 100, 1, false, []);
        Check(freshLife.Reserves().Stamina == 1 && !freshLife.Reserves().Exhausted);
        var seated = freshLife.Add(ExertionKind.Crafting, 1, seated: true)!.Value;
        freshLife.Add(ExertionKind.Crafting, 1);
        Check(Math.Abs(freshLife.Reserves().Stamina - (1 - .0028 * 1.8)) < 1e-12);
        var accepted = new WorkAcknowledgements();
        accepted.Notice(seated, 0); accepted.Credit(ExertionKind.Crafting, 1, 0);
        var unused = accepted.Settle(0);
        Check(accepted.Acknowledged == seated.Sequence && unused.Sum() == 0 && seated.Seconds == 1);
        freshLife.Reconcile(2, accepted.Acknowledged, 1 - .0028 * .8,
            BloodCirculation.LactateRate(ExertionKind.Crafting) * .8, 100, 1, false, unused);
        Check(Math.Abs(freshLife.Reserves().Stamina - (1 - .0028 * 1.8)) < 1e-12);
        Check(Math.Abs(freshLife.Reserves().Lactate - BloodCirculation.LactateRate(ExertionKind.Crafting) * 1.8) < 1e-12);
        var bounded = new WorkAcknowledgements();
        var jumping = new WorkPrediction();
        jumping.Reconcile(1, 0, 0, 20, 100, 1, true, []);
        var jump = jumping.Add(ExertionKind.Jump, 1)!.Value;
        Check(jumping.Reserves().Stamina == 0 && jumping.JumpCooldownRemaining == 3);
        jumping.Advance(1);
        jumping.Reconcile(2, 0, 0, 20, 100, 1, true, [], 0);
        Check(jumping.JumpCooldownRemaining == 2); // unacknowledged local jump survives older authority
        jumping.Reconcile(3, jump.Sequence, 0, 20, 100, 1, true, [], 0);
        Check(jumping.JumpCooldownRemaining == 0 && jumping.Reserves().Lactate == 20); // rejected reservation released
        Check(!jumping.Reconcile(2, 0, 0, 20, 100, 1, true, [], 3));
        jumping.Reconcile(4, jump.Sequence, 0, 20, 100, 1, true, [], 3, 3);
        jumping.Advance(2.99); Check(jumping.JumpCooldownRemaining > 0);
        jumping.Advance(.01); Check(jumping.JumpCooldownRemaining < 1e-9);
        bounded.Notice(new(1, ExertionKind.Crafting, double.NaN), 0);
        bounded.Notice(new(2, ExertionKind.Crafting, 100), 0);
        Check(bounded.Settle(10).Sum() == 0 && bounded.Acknowledged == 0);
    }

    public static void Reserves()
    {
        static void Check(bool value) { if (!value) throw new Exception("Exertion contract failed."); }
        foreach (var (value, expected) in new[] { (.35, 1d), (.20, .9), (.10, .8), (.05, .7), (0d, .6) })
            Check(Math.Abs(Exertion.StaminaSprintPerformance(value) - expected) < 1e-12);
        foreach (var (value, expected) in new[] { (.35, 1d), (.275, .975), (.20, .95), (.15, .825), (.10, .70), (.075, .575), (.05, .45), (0d, .25) })
            Check(Math.Abs(Exertion.StaminaLaborPerformance(value) - expected) < 1e-12);
        foreach (var value in new[] { .5, .65, .8, .85, .9, .95, 1d, 1.2 })
            Check(Exertion.LactatePerformance(value) == 1);
        foreach (var (value, expected) in new[] { (.6, 1d), (.8, .9), (.9, .8), (1d, .75) })
            Check(Math.Abs(Exertion.FatiguePerformance(value) - expected) < 1e-12);
        foreach (var (value, expected) in new[] { (.7, 1d), (.85, .95), (.95, .9), (1d, .85) })
            Check(Math.Abs(Exertion.SleepPressurePerformance(value) - expected) < 1e-12);
        foreach (var kind in new[] { ExertionKind.Sprinting, ExertionKind.Swimming, ExertionKind.Mining, ExertionKind.Bow,
            ExertionKind.MountedControl, ExertionKind.Rowing, ExertionKind.Climbing, ExertionKind.Diving,
            ExertionKind.Carrying, ExertionKind.Logging, ExertionKind.Crafting })
        {
            var once = Exertion.Apply(1, 1, kind, 10, true, true, false);
            var split = Exertion.Apply(1, 1, kind, 4, true, true, false);
            split = Exertion.Apply(split.Stamina, split.Oxygen, kind, 6, true, true, false);
            Check(Math.Abs(once.Stamina - split.Stamina) < 1e-12 && Math.Abs(once.Oxygen - split.Oxygen) < 1e-12);
            Check(once.Oxygen == 1);
            Check(kind is ExertionKind.Swimming ? once.Stamina == 1 : once.Stamina < 1);
            Check(Exertion.Apply(.5, 1, kind, 1, true, true, false).Stamina <= .5);
            if (kind is ExertionKind.MountedControl or ExertionKind.Rowing or ExertionKind.Climbing or ExertionKind.Diving
                or ExertionKind.Carrying or ExertionKind.Logging or ExertionKind.Crafting)
                Check(Exertion.Apply(.5, 1, kind, 1, true, true, false).Stamina < .5);
        }
        var floor = Exertion.Apply(1, .11, ExertionKind.Attack, 10, true, false, false);
        foreach (var kind in Enum.GetValues<ExertionKind>())
        {
            var multiplier = Exertion.WorkMultiplier(kind, true);
            Check(Exertion.WorkMultiplier(kind, false) == 1);
            if (kind is ExertionKind.Sprinting or ExertionKind.Running or ExertionKind.Jump or ExertionKind.Rowing or ExertionKind.LowPosture)
                Check(multiplier == 1);
            var working = Exertion.Apply(1, 1, kind, 1, true, false, false, workMultiplier: multiplier);
            if (kind == ExertionKind.Running)
            {
                Check(Math.Abs(working.Stamina - .991) < 1e-12);
                Check(Math.Abs(Exertion.Apply(.5, 1, kind, 1, true, false, false).Stamina - .491) < 1e-12);
            }
            else Check(Math.Abs(1 - working.Stamina - Exertion.Cost(kind).Stamina * multiplier) < 1e-12);
            Check(working.Oxygen == 1);
        }
        Check(floor.Oxygen == .11);
        Check(Exertion.Apply(1, .05, ExertionKind.Jump, 1, true, false, false).Oxygen == .05);
        Check(Math.Abs(Exertion.Apply(1, .05, ExertionKind.Jump, 1, false, false, false).Oxygen - .03) < 1e-12);
        var baselines = new (ExertionKind Kind, double Stamina, double Oxygen)[] {
            (ExertionKind.Sprinting, .01875, .02), (ExertionKind.Mining, .0225, .012),
            (ExertionKind.HeavyTool, .0225, .012), (ExertionKind.Igniting, .0225, .012),
            (ExertionKind.Logging, .0225, .012), (ExertionKind.Crafting, .0035, .001),
            (ExertionKind.Bow, .008, .003), (ExertionKind.Attack, .0625, .04), (ExertionKind.Jump, .025, .02),
            (ExertionKind.MountedControl, .006, .001), (ExertionKind.Rowing, .0125, .003),
            (ExertionKind.Climbing, .015, .015), (ExertionKind.Diving, .004, .006),
            (ExertionKind.Carrying, .01, .001), (ExertionKind.Swimming, 0, .006), (ExertionKind.LowPosture, 0, .002) };
        foreach (var baseline in baselines)
        {
            var cost = Exertion.Cost(baseline.Kind);
            Check(Math.Abs(cost.Stamina - baseline.Stamina * .8) < 1e-12 && cost.Oxygen == baseline.Oxygen);
        }
        Check(!Exertion.Exhausted(.9, 100, 100, false, false));
        Check(Exertion.Exhausted(.3, 99, 100, true, true));
        Check(!Exertion.Exhausted(.35, 59, 100, true, false));
        Check(Exertion.Exhausted(.199, 59, 100, true, false));
        Check(!Exertion.Exhausted(.20, 59, 100, true, false));
        Check(!Exertion.Exhausted(.9, 100, 100, true, false));
        Check(Exertion.Exhausted(.9, 59, 100, true, false, recovering: false));
        var clock = new RecoveryClock();
        clock.RecordWork(10);
        Check(clock.Seconds(10, 12) == 0);
        Check(clock.Seconds(12, 13) == 1);
        clock.RecordWork(13.1);
        Check(clock.Seconds(13, 15) == 0);
        Check(Math.Abs(clock.Seconds(15, 16) - .9) < 1e-12);
        var wholeClock = new RecoveryClock(); wholeClock.RecordWork(0);
        var splitClock = new RecoveryClock(); splitClock.RecordWork(0);
        double splitRest = 0;
        for (var t = 0; t < 64; t++) splitRest += splitClock.Seconds(t, t + 1);
        Check(splitRest == wholeClock.Seconds(0, 64));
        Check(Exertion.Apply(0, 0, ExertionKind.Standing, 60, true, true, false).Stamina == 1);
        Check(Exertion.Breathe(0, 45, true) == 1);
        Check(Math.Abs(Exertion.Apply(0, 1, ExertionKind.Walking, 4, true, true, false).Stamina - .048) < 1e-12);
        Check(Math.Abs(Exertion.Apply(0, 1, ExertionKind.Standing, 4, true, true, false).Stamina - .096) < 1e-12);
        Check(Math.Abs(Exertion.Apply(0, 1, ExertionKind.Sitting, 4, true, true, false).Stamina - .192) < 1e-12);
        Check(Math.Abs(Exertion.Apply(0, 1, ExertionKind.Standing, 4, true, true, false,
            options: new() { StandingRecoverySeconds = 100 }).Stamina - .048) < 1e-12);
        Check(Exertion.Apply(0, 1, ExertionKind.Walking, 200, true, true, false).Stamina == 1);
        Check(Exertion.Apply(0, 1, ExertionKind.Sitting, 25, true, true, false).Stamina == 1);
        Check(Exertion.Apply(.2, 1, ExertionKind.Walking, 4, true, true, true).Stamina == .248);
        Check(Math.Abs(Exertion.Apply(1, .1, ExertionKind.Sprinting, 40, true, false, false).Stamina - .4) < 1e-12);
        Check(Math.Abs(Exertion.Apply(1, 1, ExertionKind.Mining, 40, true, false, false).Stamina - .28) < 1e-12);
        Check(Exertion.Apply(0, .5, ExertionKind.Standing, 1, true, true, false, oxygenRecoverySeconds: 0).Oxygen == .5);
        var work = new HeldWorkLedger();
        var intervals = new WorkIntervals();
        intervals.Add(0, 2); intervals.Add(1, 3);
        Check(intervals.RestSeconds(0, 4) == 1);
        intervals.Add(4, 4.5);
        Check(intervals.RestSeconds(4, 5) == .5);
        Check(intervals.RestSeconds(5, 6) == 1);
        Check(work.Advance("fire", .2) == .2 && work.Advance("fire", .2) == 0);
        Check(Math.Abs(work.Advance("fire", .5) - .3) < 1e-12);
        Check(Math.Abs(work.Finish("fire", .6) - .1) < 1e-12);
        Check(work.Finish("fire", .6) == 0 && work.Finish("unknown", 5) == 0);
        work.End("fire");
        Check(work.Finish("fire", .7) == 0);
        Check(work.Advance("fire", .2) == .2);
        Check(BloodCirculation.Charge(new(), ExertionKind.Igniting, 100).Blood.LactateG == 0);
        foreach (var kind in new[] { ExertionKind.Crafting, ExertionKind.MountedControl,
            ExertionKind.Rowing, ExertionKind.Swimming, ExertionKind.Running, ExertionKind.Jogging })
            Check(BloodCirculation.LactateRate(kind) == 0);
        Check(Math.Abs(BloodCirculation.Charge(new(), ExertionKind.Sprinting, 3).Blood.LactateG - .3) < 1e-12);
        foreach (var kind in new[] { ExertionKind.Jump, ExertionKind.Attack, ExertionKind.Mining,
            ExertionKind.Logging, ExertionKind.HeavyTool, ExertionKind.Igniting })
            Check(BloodCirculation.Charge(new(), kind, 600).Blood.LactateG == 0);
        Check(Math.Abs(BloodCirculation.Charge(new(), ExertionKind.Sprinting, 60).Blood.LactateG - 6) < 1e-12);
        foreach (var ratio in new[] { 0d, .6, .8, .99 })
            Check(RunningPace.Fraction(1, ratio, false, new()) == 1);
        var fullLactatePace = new RunningPace();
        Check(fullLactatePace.Advance(true, true, 1, 1, 1, false, new()) == ExertionKind.Jogging);
        Check(fullLactatePace.Gain(new(), 1, 1, false) == new ExertionOptions().JoggingSpeedGain);
        Check(ExerciseMuscles.Burning(.99) == 0);
        Check(Math.Abs(Exertion.Breathe(.5, 1, true) - (.5 + 1d / 45)) < 1e-12);
        Check(Exertion.Apply(1, .5, ExertionKind.Walking, 1, true, true, false).Oxygen == .5);
        Check(Exertion.Breathe(.5, 10, false) == .5);
        Check(Math.Abs(Exertion.Breathe(.5, 9, true, .5) - .6) < 1e-12);
        Check(Math.Abs(Exertion.Breathe(Exertion.Breathe(.1, 7, true), 13, true) - Exertion.Breathe(.1, 20, true)) < 1e-12);
        var longAction = Exertion.Apply(1, .3, ExertionKind.Sprinting, 60, true, false, false);
        var partial = Exertion.Apply(1, .3, ExertionKind.Sprinting, 13, true, false, false);
        partial = Exertion.Apply(partial.Stamina, partial.Oxygen, ExertionKind.Sprinting, 47, true, false, false);
        Check(Math.Abs(longAction.Stamina - partial.Stamina) < 1e-12 && longAction.Oxygen == partial.Oxygen);
        Check(Exertion.Apply(.1, 1, ExertionKind.Standing, 1, true, false, true).Stamina == .1);
        Check(Exertion.Apply(.1, 1, ExertionKind.Standing, 1, true, true, true).SprintExhausted);
        Check(Exertion.Apply(.34, 1, ExertionKind.Standing, 1, true, true, true).SprintExhausted);
        Check(Math.Abs(Exertion.Apply(1, 1, ExertionKind.Attack, 1, true, false, false).Stamina - .95) < 1e-12);
        var profile = Seraph.Core.State.BodyProfile.Default;
        var body = Seraph.Core.State.BodyState.Create(profile);
        var lowReserve = BodyFunctionResolver.Evaluate(new() { Stamina = .15 }, body, profile);
        Check(lowReserve.CanSprint);
        Check(Exertion.StaminaLaborPerformance(.15) == .825
            && Exertion.Cost(ExertionKind.Mining).Stamina == .018
            && Math.Abs(Exertion.Apply(.15, 1, ExertionKind.Mining, 1, true, false, false).Stamina - .132) < 1e-12);
        var nearExhausted = Exertion.LoadLimit(.05, .5, 0, 0);
        Check(nearExhausted is { Value: .45, Reason: "stamina" }
            && Exertion.LoadLimit(.05, .9, 0, 0).Value == .45
            && Math.Abs(Exertion.LoadLimit(.05, .9, 0, 0, sprint: true).Value - 1) < 1e-12);
        var sober = BodyFunctionResolver.Evaluate(new(), body, profile);
        var lactate = BodyFunctionResolver.Evaluate(new() { Blood = new() { LactateG = 99 } }, body, profile);
        Check(lactate.Endurance == sober.Endurance && lactate.LaborEndurance == sober.LaborEndurance);
        Check(lactate.Mining == sober.Mining && lactate.Bow == sober.Bow && lactate.Sprint == sober.Sprint);
        Check(lactate.Mobility == sober.Mobility); // retreat remains possible
        var drunk = BodyFunctionResolver.Evaluate(new(), body, profile, nativeAlcohol: 1);
        var hallucinating = BodyFunctionResolver.Evaluate(new(), body, profile, nativePsychedelic: 2);
        Check(drunk.Balance < sober.Balance && drunk.Consciousness < sober.Consciousness);
        Check(hallucinating.Consciousness < sober.Consciousness);
    }
}
