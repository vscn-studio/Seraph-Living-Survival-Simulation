using Seraph.Core.Framework;

internal static class SanitationChecks
{
    internal static void Sources()
    {
        static void Near(double a, double b) { if (Math.Abs(a - b) > 1e-8) throw new Exception($"{a} != {b}"); }
        static FrameworkState Hour(FrameworkState f, SanitationContext c, double step = 8)
        { for (double t = 0; t < 3600; t += step) f = Sanitation.Advance(f, c, Math.Min(step, 3600 - t)); return f; }
        var f = new FrameworkState { Dirtiness = .4 };
        Near(Hour(f, new()).Dirtiness, .4);
        Near(Hour(f, new() { DirtyGround = true }).Dirtiness, .4);
        Near(Hour(f, new() { DirtyGround = true, Moving = true }).Dirtiness, .4025);
        Near(Hour(f, new() { PollutionPoints = 10 }).Dirtiness, .4025);
        var dailyWalking = new FrameworkState();
        for (var hour = 0; hour < 24; hour++)
            dailyWalking = Hour(dailyWalking, new() { DirtyGround = true, Moving = true });
        Near(dailyWalking.Dirtiness, .06);
        Near(Hour(f, new() { Raining = true }).Dirtiness, .3);
        Near(Hour(f, new() { Immersed = true }).Dirtiness, 0);
        Near(Hour(f, new() { Immersed = true, WaterDirtiness = .5 }).Dirtiness, .406);
        Near(Hour(f, new() { CarriesWaste = true }).Dirtiness, .8);
        var protectedState = f with { BathedRemainingGameHours = .3, CleanRemainingGameHours = .8 };
        var context = new SanitationContext { Moving = true, DirtyGround = true };
        var result = Hour(protectedState, context);
        Near(result.Dirtiness, .4 + (.3 * .2 + .5 * .5 + .2) * .0025);
        Near(result.Dirtiness, Hour(protectedState, context, 1).Dirtiness);
        Near(result.CleanRemainingGameHours, 0);
        foreach (var (kind, floor) in new[] { ("pond", .09), ("lake", .048), ("river", .03) })
        {
            var water = SurfaceWater.Apply(new() { VolumeL = 1 }, kind, new());
            Near(Sanitation.WaterResidue(water), floor);
            Near(Sanitation.WaterResidue(water with { EntericHazardPerL = 10,
                PathogensPerL = new Dictionary<string, double> { ["shigella"] = 50, ["tetanus"] = 20 } }), floor);
            if (!WoundCare.NeedsWash(f, 0, floor) || WoundCare.NeedsWash(f with { Dirtiness = floor }, 0, floor))
                throw new Exception("Physical water floor blocked useful washing or allowed a no-op wash.");
        }
    }
}
