using Seraph.Core.Framework;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private MeasuredSmokeAdapter? smoke;
    private IGasTransport? gases;
    private void TickSmoke(Session session, bool advancing, double dt)
    {
        session.SmokeDamageThisTick = session.ClinicalDamageThisTick = 0;
        var entity = session.Player.Entity;
        var active = advancing && settings.Config.Mode == "survival" && settings.Config.EnableClinicalEffects
            && OwnsPhysiology(entity) && !paused;
        if (!active || !double.IsFinite(dt) || dt <= 0 || dt > .5)
        { smoke?.Ownership(entity, false); session.SmokeSampleElapsed = .25; session.SmokeSampleValid = false; return; }
        if (smoke is null)
        {
            smoke = new(api); // First active world tick: all optional ModSystems have started.
            gases = GasTransport.Create(api, Pollution, channel);
            if (diagnostics is not null) ReportRuntimeCompatibility();
        }
        session.SmokeSampleElapsed += dt;
        if (session.SmokeSampleElapsed >= .25)
        {
            session.SmokeSampleElapsed = 0;
            var ready = smoke.Ready;
            session.SmokeSampleValid = smoke.TrySample(entity, out session.SmokeConcentration);
            if (ready != smoke.Ready && diagnostics is not null) ReportRuntimeCompatibility();
        }
        var measured = session.SmokeSampleValid && smoke?.Ready == true && entity.Pos.Dimension == 0;
        smoke?.Ownership(entity, measured);
        // An unavailable source is not evidence of clean air: freeze burden until measured again.
        if (!measured) return;
        var dose = SmokeInhalation.Advance(session.Framework.SmokeDose, session.SmokeConcentration,
            SmokeInhalation.Ventilation(Math.Max(session.ActivityPowerW, session.DemandPowerW))
                * AdrenalineEffects.Ventilation(session.Framework), dt);
        session.Framework = session.Framework with { SmokeDose = dose };
        session.SmokeDamageThisTick = SmokeInhalation.DamagePerRealSecond(dose) * dt * settings.Config.ClinicalDamageMultiplier;
    }

    private void SettleSmokeInjury(Session session)
    {
        // Compare accepted amounts, not rates on different clocks. Calendar acceleration
        // can amplify other clinical injury, but never the measured real-time smoke dose.
        var extra = Math.Max(0, session.SmokeDamageThisTick - session.ClinicalDamageThisTick);
        session.SmokeDamageThisTick = 0;
        if (extra <= 0 || session.Deceased || !session.Player.Entity.Alive || session.Fault is not null
            || session.Framework.Downed.Active) return;
        session.Framework = session.Framework with { Vitals = session.Framework.Vitals.Change(-extra) };
        CheckOwnDeath(session.Player.Entity);
    }
}
