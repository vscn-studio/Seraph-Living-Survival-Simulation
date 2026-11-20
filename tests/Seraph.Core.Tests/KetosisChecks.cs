using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class KetosisChecks
{
    internal static void SustainedFuel()
    {
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void Near(double a,double b) => Check(Math.Abs(a-b)<1e-10, $"Expected {a}, got {b}");
        double Integrate(double old, double seconds, double step, double glycogenFraction=0, double fatShare=1)
        {
            for (double t=0;t<seconds;t+=step)
                old=Ketosis.Advance(old,glycogenFraction,(1-fatShare)*step,fatShare*step,0,Math.Min(step,seconds-t));
            return old;
        }
        Near(0, Integrate(0,21600,8,.1));
        Near(0, Integrate(0,21600,8,0,.5));
        var sixHours=Integrate(0,21600,8);
        Near(1-Math.Exp(-1),sixHours);
        Near(sixHours,Integrate(0,21600,1));
        Check(!Ketosis.Symptomatic(Integrate(0,600,8)), "Brief fat use caused symptoms.");
        Near(.25*(1-Math.Exp(-1)),Integrate(0,21600,8,.05,.75));
        Near(0, Ketosis.Advance(0,0,0,0,0,8));
        Near(.6, Ketosis.Advance(.6,0,0,8,0,0));
        var decay=sixHours;
        for(var t=0;t<7200;t+=8) decay=Ketosis.Advance(decay,0,0,0,0,8);
        Near(sixHours*Math.Exp(-1),decay);
        Near(1,Ketosis.Performance(.25)); Near(.85,Ketosis.Performance(1)); Near(.925,Ketosis.Performance(.625));
        var body=BodyState.Create() with { GlycogenEnergyKJ=0 };
        var state=new FrameworkState { OxygenKnown=true, Clinical=new() { Ketosis=1 } };
        Near(.85, PlayerCondition.Evaluate(state,body,BodyProfile.Default).StaminaRecoveryMultiplier);
        Near(.85, BodyFunctionResolver.Evaluate(state,body,BodyProfile.Default).Mining);
        var loaded=state with { Stamina=.05, Fatigue=.8 };
        Near(.45, BodyFunctionResolver.Evaluate(loaded,body,BodyProfile.Default).Mining);
        var normal=state with { Clinical=state.Clinical with { Ketosis=0 } };
        Check(!ClinicalEffects.Evaluate(normal,body,BodyProfile.Default).Any(e=>e.Id=="ketosis"), "Empty glycogen caused immediate ketosis.");
        Check(ClinicalEffects.Evaluate(state,body,BodyProfile.Default).Single(e=>e.Id=="ketosis").Severity==1, "Ketosis symptom missing.");
        Near(.8, ClinicalIndicatorsModel.Advance(new(){ Ketosis=.8 }, normal,body,BodyProfile.Default,8,1,0).Ketosis);
    }
}
