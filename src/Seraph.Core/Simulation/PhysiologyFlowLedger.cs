namespace Seraph.Core.Simulation;

/// <summary>Actual accepted flows for one published settlement window, including every labor/rest segment.</summary>
public sealed record PhysiologyFlowLedger
{
    public double Seconds { get; init; }
    public double BasalJ { get; init; }
    public double ColdJ { get; init; }
    public double ActivityJ { get; init; }
    public double RecoveryJ { get; init; }
    public double AbsorbedWaterL { get; init; }
    public double LostWaterL { get; init; }
    public double GlycogenKJ { get; init; }
    public double FatKJ { get; init; }
    public double MuscleKJ { get; init; }
    public double WasteProducedMl { get; init; }
    public double WasteClearedMl { get; init; }
    public double Hourly(double amount) => Seconds > 0 ? amount * 3600 / Seconds : 0;
    public double KcalPerHour(double joules) => Hourly(joules) / 4184;
    public PhysiologyFlowLedger Add(SimulationDiagnostics d, double recoveryKJ, double producedMl, double clearedMl,
        double recoveryGlycogenKJ = 0, double recoveryFatKJ = 0)
        => this with {
            Seconds = Seconds + d.AcceptedBioSeconds,
            BasalJ = BasalJ + d.BasalEnergyJ, ColdJ = ColdJ + d.ShiveringEnergyJ,
            ActivityJ = ActivityJ + d.ActivityEnergyJ, RecoveryJ = RecoveryJ + recoveryKJ * 1000,
            AbsorbedWaterL = AbsorbedWaterL + d.AbsorbedWaterL,
            LostWaterL = LostWaterL + d.SweatWaterL + d.InsensibleWaterL + d.RespiratoryWaterL
                + d.FecalWaterL + d.EntericWaterLossL + d.BloodWaterLossL,
            GlycogenKJ = GlycogenKJ + d.GlycogenConsumedKJ + Math.Max(0, recoveryGlycogenKJ),
            FatKJ = FatKJ + d.FatConsumedKJ + Math.Max(0, recoveryFatKJ), MuscleKJ = MuscleKJ + d.MuscleFuelKJ,
            WasteProducedMl = WasteProducedMl + Math.Max(0, producedMl),
            WasteClearedMl = WasteClearedMl + Math.Max(0, clearedMl)
        };
}
