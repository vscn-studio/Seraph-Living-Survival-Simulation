namespace Seraph.Core.Framework;

/// <summary>Extra contamination in one equal-volume water block; natural baselines are separate.</summary>
public sealed record PondContamination
{
    public double Generic { get; init; }
    public IReadOnlyDictionary<string, double> Agents { get; init; } = PathogenLoads.Empty;
    public bool Valid() => double.IsFinite(Generic) && Generic is >= 0 and <= 100 && PathogenLoads.Valid(Agents);
    public PondContamination Scale(double factor) => new() {
        Generic = Math.Clamp(Generic * factor, 0, 100), Agents = PathogenLoads.Scale(Agents, factor) };
    public PondContamination Add(PondContamination other) => new() {
        Generic = Math.Min(100, Generic + other.Generic), Agents = PathogenLoads.Add(Agents, other.Agents) };
}

/// <summary>Exact decay and conservative exchange with the body mean, for equal-volume cells.</summary>
public static class PondContaminationModel
{
    public const double HalfLifeHours = 12;
    public const double MixingHours = 2;
    public static PondContamination Mean(IReadOnlyList<PondContamination> cells)
    {
        if (cells.Count == 0) return new();
        var generic = cells.Sum(c => c.Generic) / cells.Count;
        var agents = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var cell in cells)
            foreach (var p in cell.Agents) agents[p.Key] = agents.GetValueOrDefault(p.Key) + p.Value / cells.Count;
        return new() { Generic = generic, Agents = agents };
    }
    public static PondContamination AdvanceCell(PondContamination cell, PondContamination mean,
        PondContamination inputPerHour, PondContamination meanInputPerHour, double hours)
    {
        if (!double.IsFinite(hours) || hours < 0 || !cell.Valid() || !mean.Valid()
            || !inputPerHour.Valid() || !meanInputPerHour.Valid()) throw new ArgumentException("Invalid pond transport.");
        var decay = Math.Log(2) / HalfLifeHours;
        var exchange = 1 / MixingHours;
        var keepMean = Math.Exp(-decay * hours);
        var keepLocal = Math.Exp(-(decay + exchange) * hours);
        double Next(double local, double average, double input, double averageInput) => Math.Clamp(
            average * keepMean + averageInput * (1 - keepMean) / decay
            + (local - average) * keepLocal + (input - averageInput) * (1 - keepLocal) / (decay + exchange), 0, 100);
        return new() {
            Generic = Next(cell.Generic, mean.Generic, inputPerHour.Generic, meanInputPerHour.Generic),
            Agents = cell.Agents.Keys.Concat(mean.Agents.Keys).Concat(inputPerHour.Agents.Keys)
                .Concat(meanInputPerHour.Agents.Keys).Distinct(StringComparer.Ordinal).ToDictionary(id => id,
                    id => Next(cell.Agents.GetValueOrDefault(id), mean.Agents.GetValueOrDefault(id),
                        inputPerHour.Agents.GetValueOrDefault(id), meanInputPerHour.Agents.GetValueOrDefault(id))) };
    }
}
