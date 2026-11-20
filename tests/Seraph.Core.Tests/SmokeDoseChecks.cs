using Seraph.Core.Framework;
using Seraph.Core.State;
using System.Text.Json;

internal static class SmokeDoseChecks
{
    internal static void Dose()
    {
        static void Near(double a, double b) { if (Math.Abs(a - b) > 1e-8) throw new Exception($"{a} != {b}"); }
        double Advance(double initial, double c, double ventilation, int seconds)
        {
            for (var i = 0; i < seconds * 4; i++) initial = SmokeInhalation.Advance(initial, c, ventilation, .25);
            return initial;
        }
        Near(Advance(0, 1, 8, 60), 60);
        Near(Advance(0, 1, 40, 60), 300);
        Near(SmokeInhalation.Ventilation(0), 8); Near(SmokeInhalation.Ventilation(600), 40);
        Near(Advance(600, 0, 8, 180), 300);
        Near(SmokeInhalation.Advance(600, 1, 40, 0), 600);
        Near(SmokeInhalation.Capacity(60), 1); Near(SmokeInhalation.Capacity(180), .875);
        Near(SmokeInhalation.Capacity(300), .75); Near(SmokeInhalation.Capacity(600), .75);
        Near(SmokeInhalation.DamagePerRealSecond(300), 0); Near(SmokeInhalation.DamagePerRealSecond(600), 1d / 60);
        var frame = new FrameworkState { SmokeDose = 300 };
        var function = BodyFunctionResolver.Evaluate(frame, BodyState.Create(), BodyProfile.Default);
        Near(function.RespiratoryCapacity, .75);
        if (function.RespiratoryReason != "smoke") throw new Exception("Missing actual respiratory cause");
        var gas = frame with { CarbonMonoxideBurden = 1 };
        Near(BodyFunctionResolver.Evaluate(gas, BodyState.Create(), BodyProfile.Default).RespiratoryCapacity, .45);
        Near(JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(frame))!.SmokeDose, 300);
        Near(JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(FrameworkState.Create("release-defaults")))!.SmokeDose, 0);
        if ((frame with { SmokeDose = double.NaN }).Validate() is null) throw new Exception("Invalid dose accepted");
        if (ClinicalEffects.Evaluate(frame with { SmokeDose = 59.99 }, BodyState.Create(), BodyProfile.Default).Any(e => e.Id == "smoke-inhalation"))
            throw new Exception("Warning below dose threshold");
        if (!ClinicalEffects.Evaluate(frame, BodyState.Create(), BodyProfile.Default).Any(e => e.Id == "smoke-inhalation"))
            throw new Exception("Missing smoke warning");
        Near(Advance(3599, 100, 40, 60), SmokeInhalation.MaximumDose);
    }
}
