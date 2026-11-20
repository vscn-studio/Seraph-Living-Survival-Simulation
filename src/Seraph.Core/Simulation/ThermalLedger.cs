namespace Seraph.Core.Simulation;

/// <summary>Read-only rates of accepted thermal flows. Outward heat is positive, including signed environmental gain.</summary>
public sealed record ThermalLedger(double Seconds = 0, double HeatInJ = 0, double HeatOutJ = 0,
    double ShiveringJ = 0, double SweatL = 0)
{
    public ThermalLedger Add(SimulationDiagnostics d) => new(Seconds + d.AcceptedBioSeconds,
        HeatInJ + d.MetabolicEnergyJ - d.ExternalWorkJ,
        HeatOutJ + d.DryHeatOutJ + d.RespiratoryHeatOutJ + d.EvaporativeHeatOutJ + d.ContactHeatOutJ,
        ShiveringJ + d.ShiveringEnergyJ, SweatL + d.SweatWaterL);
    public double HeatInW => Seconds > 0 ? HeatInJ / Seconds : 0;
    public double HeatOutW => Seconds > 0 ? HeatOutJ / Seconds : 0;
    public double NetW => HeatInW - HeatOutW;
    public double ShiveringW => Seconds > 0 ? ShiveringJ / Seconds : 0;
    public double SweatLPerGameHour => Seconds > 0 ? SweatL * 3600 / Seconds : 0;
}
