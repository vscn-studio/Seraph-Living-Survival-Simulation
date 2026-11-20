namespace Seraph.Core.Framework;

/// <summary>Bounded agent concentrations. Unknown scalar pollution stays generic.</summary>
public static class PathogenLoads
{
    public static IReadOnlyDictionary<string, double> Empty { get; } = new Dictionary<string, double>();
    public static bool Valid(IReadOnlyDictionary<string, double>? values, double maximum = 100)
        => values is not null && values.Count <= 32 && values.All(p => Pathogens.Definitions.ContainsKey(p.Key)
            && double.IsFinite(p.Value) && p.Value >= 0 && p.Value <= maximum);
    public static IReadOnlyDictionary<string, double> Scale(IReadOnlyDictionary<string, double> values, double factor)
        => values.Where(p => p.Value * factor > 1e-12).ToDictionary(p => p.Key, p => Math.Clamp(p.Value * factor, 0, 100), StringComparer.Ordinal);
    public static IReadOnlyDictionary<string, double> Add(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
        => a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal).ToDictionary(k => k,
            k => Math.Min(100, a.GetValueOrDefault(k) + b.GetValueOrDefault(k)), StringComparer.Ordinal);
    public static IReadOnlyDictionary<string, double> Mix(IReadOnlyDictionary<string, double> a, double amountA,
        IReadOnlyDictionary<string, double> b, double amountB)
        => amountA + amountB <= 0 ? Empty : Add(Scale(a, amountA / (amountA + amountB)), Scale(b, amountB / (amountA + amountB)));

    public static IReadOnlyDictionary<string, double> WithGenericEnteric(IReadOnlyDictionary<string, double> identified, double generic)
    {
        if (generic <= 0 || identified.Any(p => p.Value > 0 && p.Key != "water-enteric" && Pathogens.Enteric(p.Key))) return identified;
        var existing = identified.GetValueOrDefault("water-enteric");
        if (existing > 0)
            return identified.ToDictionary(p => p.Key, p => p.Key == "water-enteric" ? Math.Max(existing, generic) : p.Value,
                StringComparer.Ordinal);
        return Add(identified, new Dictionary<string, double> { ["water-enteric"] = generic });
    }
}
