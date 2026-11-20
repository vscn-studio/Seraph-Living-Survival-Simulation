using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class RunningChecks
{
    public static void PaceAndReserves()
    {
        var options = new ExertionOptions();
        var pace = new RunningPace();
        Check(pace.Advance(true, true, 3, 1, 0, false, options) == ExertionKind.Sprinting);
        Near(1, pace.Gain(options));
        pace.Advance(true, true, .5, 1, 0, false, options);
        Near(.9, pace.Gain(options));
        Check(pace.Advance(true, true, .5, 1, 0, false, options) == ExertionKind.Running);
        Near(.8, pace.Gain(options));
        pace.Advance(false, true, .1, 1, 0, false, options);
        Check(pace.Advance(true, true, .1, 1, 0, false, options) == ExertionKind.Running);
        pace.Advance(false, true, 2, 1, 0, false, options);
        Check(pace.Advance(true, true, .1, 1, 0, false, options) == ExertionKind.Sprinting);
        Check(pace.Advance(true, true, .1, 0, 0, true, options) == ExertionKind.Jogging);
        pace.Advance(false, true, 2, 1, 0, false, options);
        pace.Advance(true, false, 3, 1, 0, false, options);
        Check(pace.Advance(true, true, .1, 1, 0, false, options) == ExertionKind.Running);
        var restored = new RunningPace();
        restored.Restore(pace.Held, pace.Elapsed, pace.Released, pace.Burst, pace.Limit);
        Near(pace.Gain(options), restored.Gain(options));

        var body = BodyState.Create();
        Check(BodyFunctionResolver.Evaluate(new() { Stamina = 0 }, body, BodyProfile.Default).CanSprint);
        var stamina = 0d;
        for (var i = 0; i < 2400; i++)
            stamina = Exertion.Apply(stamina, 1, ExertionKind.Running, .25, true, false, true).Stamina;
        Near(0, stamina);
        Near(0, Exertion.Apply(0, 1, ExertionKind.Running, 10, false, false, true).Stamina);
        Near(.9, Exertion.Apply(1, 1, ExertionKind.Running, 10, false, false, false).Oxygen);
        Check(Exertion.StaminaDelta(ExertionKind.Running, 1, .5) < 0);
        Near(1, Exertion.RunningLoad(0, 1, 0, 0));
        Near(1, Exertion.LoadLimit(0, 1, 0, 0, sprint: true).Value);
        Check(Exertion.MovementPowerW(ExertionKind.Running) > Exertion.MovementPowerW(ExertionKind.Walking));
        var prediction = new WorkPrediction();
        Check(prediction.Reconcile(1, 0, .2, 5, 100, 1, false, []));
        prediction.Add(ExertionKind.Running, 1);
        Near(.191, prediction.Reserves().Stamina);
        Near(5, prediction.Reserves().Lactate);
        Check(prediction.Reconcile(2, 1, .191, 5, 100, 1, false, []));
        Near(.191, prediction.Reserves().Stamina);
    }

    public static void LactateAndSoreness()
    {
        Near(0, BloodCirculation.LactateRate(ExertionKind.Running));
        Near(0, BloodCirculation.LactateRate(ExertionKind.Running, .5));
        Check(BloodCirculation.LactateRate(ExertionKind.Sprinting) > 0);
        var loaded = new FrameworkState { Blood = new() { LactateG = 80 } };
        var circulation = new CirculationOptions();
        var whole = BloodCirculation.RecoverExercise(loaded, ExertionKind.Standing, 180, 1, circulation);
        Near(40, whole.Blood.LactateG);
        var split = loaded;
        for (var i = 0; i < 720; i++)
            split = BloodCirculation.RecoverExercise(split, ExertionKind.Standing, .25, 1, circulation);
        Near(whole.Blood.LactateG, split.Blood.LactateG);
        Near(80, BloodCirculation.RecoverExercise(loaded, ExertionKind.Running, 180, 1, circulation).Blood.LactateG);
        Near(80, BloodCirculation.RecoverExercise(loaded, ExertionKind.Standing, 180, 0, circulation).Blood.LactateG);
        Near(0, loaded.Blood.WasteMl);

        whole = ExerciseMuscles.Charge(new(), ExertionKind.Running, 1800, 1);
        split = new();
        for (var i = 0; i < 7200; i++)
            split = ExerciseMuscles.Charge(split, ExertionKind.Running, .25, 1);
        Near(whole.ExerciseLoad, split.ExerciseLoad);
        Near(whole.MuscleSoreness, split.MuscleSoreness);
        Check(whole.MuscleSoreness > 0 && whole.Wounds.Length == 0);
        var sore = whole;
        Near(sore.MuscleSoreness / 2, ExerciseMuscles.Recover(sore, 3600, true, false, false).MuscleSoreness);
        Near(sore.MuscleSoreness / 2, ExerciseMuscles.Recover(sore, 7200, false, true, false).MuscleSoreness);
        Near(sore.MuscleSoreness, ExerciseMuscles.Recover(sore, 7200, false, false, true).MuscleSoreness);
        var cleared = BloodCirculation.RecoverExercise(sore with { Blood = new() { LactateG = 80 } },
            ExertionKind.Standing, 3600, 1, circulation);
        Near(sore.MuscleSoreness, cleared.MuscleSoreness);
        Near(0, Treatments.Pain(loaded with { Blood = new() { LactateG = 50 } }, 50));
        var saved = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(sore))!;
        Near(sore.MuscleSoreness, saved.MuscleSoreness);
        Near(sore.ExerciseLoad, saved.ExerciseLoad);
        var legacy = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(FrameworkState.Create("release-defaults")))!;
        Near(0, legacy.MuscleSoreness);
        Check(legacy.Validate() is null);
        var soreFunctions = BodyFunctionResolver.Evaluate(new() { MuscleSoreness = 1 }, BodyState.Create(), BodyProfile.Default);
        Near(.95, soreFunctions.Mobility);
        Near(.9, soreFunctions.Sprint);
        Check(soreFunctions.CanSprint && soreFunctions.CanJump);
        // Ordinary calendar speed must still permit soreness to accumulate during a long run.
        var simulated = new FrameworkState();
        for (var i = 0; i < 2400; i++)
        {
            simulated = ExerciseMuscles.Charge(simulated, ExertionKind.Running, .25, 1);
            simulated = ExerciseMuscles.Recover(simulated, 7.5, false, false, true);
        }
        Check(simulated.MuscleSoreness > .005);
    }

    public static void BoundedRunning()
    {
        var options = new ExertionOptions();
        Near(.5, RunningPace.Fraction(.1, 0, false, options));
        Near(1, RunningPace.Fraction(1, .8, false, options));
        Near(.5, RunningPace.Fraction(.1, .8, false, options));
        Near(0, RunningPace.Fraction(1, 0, true, options));
        (double SlowAt, double EmptyAt, double Reserve) Simulate(double step)
        {
            var pace = new RunningPace();
            var stamina = 1d; var slowAt = 0d; var emptyAt = 0d;
            var exhausted = false;
            for (var t = 0d; t < 720 - 1e-8; t += step)
            {
                var kind = pace.Advance(true, true, step, stamina, 0, exhausted, options);
                var before = stamina;
                var burst = pace.BurstChargeSeconds;
                if (burst > 0) stamina = Exertion.Apply(stamina, 1, ExertionKind.Sprinting, burst, true, false, exhausted).Stamina;
                if (step > burst) stamina = Exertion.Apply(stamina, 1, kind, step - burst, true, true, exhausted).Stamina;
                Check(stamina <= before + 1e-12);
                exhausted |= stamina <= 0;
                if (slowAt == 0 && stamina < .2) slowAt = t + step;
                if (emptyAt == 0 && stamina == 0) emptyAt = t + step;
                if (t > 110) { Check(kind == ExertionKind.Jogging); Near(.25, pace.Gain(options)); }
            }
            Near(0, stamina);
            // A refill alone cannot unlock a held key.
            Check(pace.Advance(true, true, 1, 1, 0, true, options) == ExertionKind.Jogging);
            Near(.25, pace.Gain(options));
            Check(Exertion.Exhausted(.2, 0, 100, true, true));
            Check(!Exertion.Exhausted(.2, 0, 100, true, false));
            pace.Advance(false, true, 2, .2, 0, false, options);
            Check(pace.Advance(true, true, .1, .2, 0, false, options) == ExertionKind.Sprinting);
            return (slowAt, emptyAt, stamina);
        }
        foreach (var step in new[] { .05, .25, .7, 1d })
        {
            var run = Simulate(step);
            Check(run.SlowAt >= 86 && run.SlowAt <= 87.5);
            Check(run.EmptyAt >= 108 && run.EmptyAt <= 109.5);
        }
        var sudden = new RunningPace();
        sudden.Advance(true, true, 4, 1, 0, false, options);
        sudden.Advance(true, true, .1, 1, 1, true, options);
        Near(.25, sudden.Gain(options));
        sudden.Advance(true, true, .9, 1, 1, true, options);
        Near(.25, sudden.Gain(options));
        Near(.1, Exertion.Apply(.1, 1, ExertionKind.Jogging, 600, true, true, true).Stamina);
        Near(.012, Exertion.Apply(0, 1, ExertionKind.Walking, 1, true, true, true).Stamina);
        Near(300, Exertion.MovementPowerW(ExertionKind.Jogging) * 3600 / 4184);
        Near(375, Exertion.MovementPowerW(ExertionKind.Running, .5) * 3600 / 4184);
        Near(0, BloodCirculation.LactateRate(ExertionKind.Jogging));
        Check(Exertion.Apply(1, 1, ExertionKind.Running, 50, true, false, false, capacity: .5).Stamina
            < Exertion.Apply(1, 1, ExertionKind.Running, 50, true, false, false).Stamina);
        var prediction = new WorkPrediction();
        prediction.Reconcile(1, 0, .1, 80, 100, .5, false, []);
        prediction.Add(ExertionKind.Running, 1);
        var actual = BloodCirculation.Charge(new() { Blood = new() { LactateG = 80 } }, ExertionKind.Running, 1, 100, .5, .5);
        Near(actual.Blood.LactateG, prediction.Reserves().Lactate);
        Near(.082, prediction.Reserves().Stamina);
    }

    private static void Check(bool value) { if (!value) throw new Exception("Running contract failed."); }
    private static void Near(double expected, double actual)
    { if (Math.Abs(expected - actual) > 1e-10) throw new Exception($"Expected {expected}, got {actual}."); }
}
