using Seraph.Core.State;

namespace Seraph.Core.Framework;

public sealed record AmbientExposureClock(double Hazard, double Threshold, ulong RandomState = 1);
public sealed record ExposureLedgerEntry(string Route, string Source, double Input, double Retained, double Overflow, double Cleared);
public sealed record ExposureBarrierState
{
    public IReadOnlyDictionary<string, double> Skin { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> Inhaled { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> Ingested { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, AmbientExposureClock> Ambient { get; init; } = new Dictionary<string, AmbientExposureClock>();
    public ExposureLedgerEntry[] Ledger { get; init; } = [];
    public string? Validate() => !PathogenLoads.Valid(Skin, 200) || !PathogenLoads.Valid(Inhaled, 100)
        || !PathogenLoads.Valid(Ingested, 100) || Skin.Values.Sum() > 200.000001
        || Inhaled.Values.Sum() > 100.000001 || Ingested.Values.Sum() > 100.000001
        || Ambient is null || Ambient.Count > 32 || Ambient.Any(p => !Pathogens.Definitions.ContainsKey(p.Key)
            || p.Value is null || p.Value.RandomState == 0 || !BodyProfile.In(p.Value.Hazard, 0, 100) || !BodyProfile.In(p.Value.Threshold, double.Epsilon, 100))
        || Ledger is null || Ledger.Length > 32 || Ledger.Any(p => p is null || !TreatmentDefinition.Key(p.Route)
            || !TreatmentDefinition.Key(p.Source) || new[] { p.Input, p.Retained, p.Overflow, p.Cleared }.Any(v => !BodyProfile.In(v, 0, 1e12)))
        ? "Invalid exposure barrier state." : null;
}

/// <summary>Shared capacity per entry route, with identified species conserved through capture and clearance.</summary>
public static class ExposureBarriers
{
    public const double UnitsPerHazard = 100;
    // Digestion is a partial biological filter. A large hard bucket alone erased
    // every ordinary drinking dose between meals; retain capacity for overloads.
    public const double IngestionPassThrough = .65;
    public static double ContactUnits(double concentration, double amount) => concentration * amount * UnitsPerHazard;
    private static readonly ExposureRoute[] Routes = [ExposureRoute.Skin, ExposureRoute.Airborne, ExposureRoute.Ingestion];
    private static IReadOnlyDictionary<string, double> Scale(IReadOnlyDictionary<string, double> loads, double factor)
        => loads.Where(p => p.Value * factor > 1e-12).ToDictionary(p => p.Key, p => p.Value * factor);
    private static IReadOnlyDictionary<string, double> Merge(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
        => a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal).ToDictionary(k => k, k => a.GetValueOrDefault(k) + b.GetValueOrDefault(k));
    public static double Capacity(FrameworkState state, ExposureRoute route) => route == ExposureRoute.Skin
        ? state.Wounds.Any(w => w.Healing < 1 && w.BleedLps > 0 && w.BandageQuality <= 0 && WoundCare.Open(w.Kind, w.BleedLps)) ? 75 : 200 : 100;
    private static IReadOnlyDictionary<string, double> Loads(FrameworkState s, ExposureRoute route) => route switch {
        ExposureRoute.Skin => s.Barriers.Skin, ExposureRoute.Airborne => s.Barriers.Inhaled,
        ExposureRoute.Ingestion => s.Barriers.Ingested, _ => PathogenLoads.Empty };
    private static FrameworkState Set(FrameworkState s, ExposureRoute route, IReadOnlyDictionary<string, double> loads)
        => s with { Barriers = route switch {
            ExposureRoute.Skin => s.Barriers with { Skin = loads }, ExposureRoute.Airborne => s.Barriers with { Inhaled = loads },
            ExposureRoute.Ingestion => s.Barriers with { Ingested = loads }, _ => s.Barriers } };
    private static FrameworkState Log(FrameworkState s, ExposureLedgerEntry e)
        => s with { Barriers = s.Barriers with { Ledger = s.Barriers.Ledger.TakeLast(31).Append(e).ToArray() } };
    private static FrameworkState Internal(FrameworkKernel kernel, FrameworkState s, ExposureRoute route, IReadOnlyDictionary<string, double> loads, string source)
    {
        foreach (var load in loads.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (load.Value <= 0 || !Pathogens.Definitions.TryGetValue(load.Key, out var definition)) continue;
            // Carriage is not infection: skin contact cannot swallow enteric agents.
            var allowed = route switch {
                ExposureRoute.Ingestion => Pathogens.Enteric(load.Key),
                ExposureRoute.Airborne => Pathogens.Airborne(load.Key),
                ExposureRoute.Skin => load.Key is "skin-fungal" or "anthrax"
                    || Pathogens.Wound(load.Key) && s.Wounds.Any(w => w.Healing < 1 && WoundCare.Open(w.Kind, w.BleedLps)),
                _ => false };
            if (!allowed) continue;
            var result = kernel.Expose(s, definition, Math.Min(100, load.Value / UnitsPerHazard), 1, source);
            if (!result.Accepted) throw new InvalidOperationException(result.Error);
            s = result.State;
        }
        return s;
    }
    public static FrameworkState Add(FrameworkKernel kernel, FrameworkState state, ExposureRoute route,
        IReadOnlyDictionary<string, double> incoming, string source)
    {
        if (!Routes.Contains(route) || !PathogenLoads.Valid(incoming, 1e9) || !TreatmentDefinition.Key(source)) throw new ArgumentException("Invalid exposure input.");
        state = Trim(kernel, state, route);
        var total = incoming.Values.Sum();
        if (total <= 0) return state;
        var old = Loads(state, route);
        var capture = Math.Min(route == ExposureRoute.Ingestion ? 1 - IngestionPassThrough : 1,
            Math.Clamp((Capacity(state, route) - old.Values.Sum()) / total, 0, 1));
        var retained = Scale(incoming, capture);
        var overflow = Scale(incoming, 1 - capture);
        state = Set(state, route, Merge(old, retained));
        state = Internal(kernel, state, route, overflow, source);
        return Log(state, new(route.ToString(), source, total, retained.Values.Sum(), overflow.Values.Sum(), 0));
    }
    private static FrameworkState Trim(FrameworkKernel kernel, FrameworkState state, ExposureRoute route)
    {
        var old = Loads(state, route); var total = old.Values.Sum(); var cap = Capacity(state, route);
        if (total <= cap) return state;
        state = Set(state, route, Scale(old, cap / total));
        state = Internal(kernel, state, route, Scale(old, 1 - cap / total), "barrier-capacity-loss");
        return Log(state, new(route.ToString(), "barrier-capacity-loss", 0, 0, total - cap, 0));
    }
    public static FrameworkState Advance(FrameworkKernel kernel, FrameworkState state, double seconds)
    {
        if (!BodyProfile.In(seconds, 0, 8)) throw new ArgumentOutOfRangeException(nameof(seconds));
        foreach (var route in Routes)
        {
            state = Trim(kernel, state, route);
            var old = Loads(state, route); var total = old.Values.Sum();
            if (total == 0 || seconds == 0) continue;
            var cleared = Math.Min(total, (route == ExposureRoute.Skin ? 10 : 5) * seconds / 3600);
            state = Set(state, route, Scale(old, Math.Max(0, 1 - cleared / total)));
            state = state with { Blood = state.Blood with { WasteMl = Math.Min(1e9, state.Blood.WasteMl + cleared * .25) } };
            state = Log(state, new(route.ToString(), "immune-clearance", 0, 0, 0, cleared));
        }
        return state;
    }
    public static FrameworkState Wash(FrameworkState state) => state with {
        Barriers = state.Barriers with { Skin = Scale(state.Barriers.Skin, .1) } };
}
