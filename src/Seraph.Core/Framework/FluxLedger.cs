namespace Seraph.Core.Framework;

public enum FluxUnit { Joule, KiloJoule, Litre, Kilogram }
public sealed record FluxEntry(string Source, string Destination, double Amount, FluxUnit Unit, string Cause);

/// <summary>Per-transition accounting. Internal transfers cancel; only bounded finite fluxes are recorded.</summary>
public sealed class FluxLedger
{
    private readonly List<FluxEntry> entries = [];
    public IReadOnlyList<FluxEntry> Entries => entries.AsReadOnly();
    public void Transfer(string source, string destination, double amount, FluxUnit unit, string cause)
    {
        if (entries.Count >= 256 || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)
            || string.IsNullOrWhiteSpace(cause) || !double.IsFinite(amount) || amount < 0 || !Enum.IsDefined(unit))
            throw new ArgumentException("Invalid flux or transition budget exceeded.");
        entries.Add(new(source, destination, amount, unit, cause));
    }
    public double Net(string store, FluxUnit unit) => entries.Where(e => e.Unit == unit)
        .Sum(e => (e.Destination == store ? e.Amount : 0) - (e.Source == store ? e.Amount : 0));
}
