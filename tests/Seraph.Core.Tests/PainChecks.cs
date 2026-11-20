using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class PainChecks
{
    public static void NaturalDecay()
    {
        void Near(double a, double b) { if (Math.Abs(a - b) > 1e-10) throw new Exception($"Pain {a} != {b}"); }
        var options = new PainOptions();
        var wound = new Wound { Severity = 1, Kind = WoundKind.Cut, Healing = .2 };
        var pools = WoundPain.Pools(wound);
        var whole = WoundPain.Advance(wound, 7200, options);
        var split = WoundPain.Advance(WoundPain.Advance(wound, 3600, options), 3600, options);
        Near(pools.Fast / 2, whole.PainPools!.Fast);
        Near(pools.Residual, whole.PainPools.Residual);
        Near(WoundPain.Contribution(whole), WoundPain.Contribution(split));
        Near(.2, whole.Healing); // Decay must not fabricate wound closure or consume nutrition.
        var burn = WoundPain.Advance(wound with { Kind = WoundKind.Burn }, 21600, options);
        Near(pools.Fast / 2, burn.PainPools!.Slow);
        var unpaid = WoundPain.PaidHealing(wound, whole with { Healing = .9 }, .2);
        Near(pools.Residual, unpaid.PainPools!.Residual);
        var paid = WoundPain.PaidHealing(wound, whole, .6);
        Near(pools.Residual / 2, paid.PainPools!.Residual);
        var merged = new Wound { PainPools = WoundPain.Merge([whole, burn]) };
        Near(WoundPain.Resolve([whole, burn]), WoundPain.Contribution(merged));
        var frame = new FrameworkState { Wounds = [whole] };
        Near(BodyFunctionResolver.Pain(frame.Wounds), Treatments.Pain(frame));
        var restored = System.Text.Json.JsonSerializer.Deserialize<Wound>(System.Text.Json.JsonSerializer.Serialize(whole))!;
        Near(WoundPain.Contribution(whole), WoundPain.Contribution(restored));
    }
}
