namespace Seraph.Core.Framework;

public enum WasteContainment { Exposed, OpenWatertight, ClosedStorage, SealedWatertight }
public enum OrganicWasteKind { Fecal, Corpse, Rot, ButcherRemains, Compost }

/// <summary>Physical containment, independent of whether a player has an inventory GUI open.</summary>
public static class PollutionTransport
{
    public static double GasEscape(WasteContainment containment) => containment switch {
        WasteContainment.SealedWatertight => 0, WasteContainment.ClosedStorage => .25,
        WasteContainment.OpenWatertight => .6, _ => 1 };
    public static double WaterEscape(WasteContainment containment) => containment switch {
        WasteContainment.SealedWatertight or WasteContainment.OpenWatertight => 0,
        WasteContainment.ClosedStorage => .25, _ => 1 };
    public static bool Leaks(WasteContainment containment) => WaterEscape(containment) > 0;
    public static AirChemistry OrganicGas(OrganicWasteKind kind, double quantity)
    {
        var (ammonia, sulfide) = kind switch {
            OrganicWasteKind.Fecal => (.55, .15), OrganicWasteKind.Corpse => (.30, .35),
            OrganicWasteKind.ButcherRemains => (.40, .25), OrganicWasteKind.Compost => (.25, .10), _ => (.20, .20) };
        return new AirChemistry(0, 0, 0, 0, ammonia, 0, sulfide).Scale(Math.Max(0, quantity) * .03);
    }
    public static double VegetationRetention(double units, double seconds, bool soot = false)
        => Math.Pow(soot ? .9 : .95, Math.Clamp(units / 32, 0, 1) * Math.Max(0, seconds) / 60);
    /// <summary>Per-gas foliar removal: ammonia binds readily; sulfur dioxide removal is lower.</summary>
    public static double VegetationRetention(double units, double seconds, int gasChannel)
    {
        var perMinute = gasChannel switch { 0 => .95, 1 => .97, 2 => .85, 3 => .90, _ => 1 };
        return Math.Pow(perMinute, Math.Clamp(units / 32, 0, 1) * Math.Max(0, seconds) / 60);
    }
    public static double AquaticRetention(double maturePlants, double hours)
        => Math.Pow(.5, Math.Clamp(maturePlants / 16, 0, 1) * Math.Max(0, hours) / 48);
    public static double RiparianRetention(double units) => 1 - .3 * Math.Clamp(units / 8, 0, 1);
    public static double CompostActivity(double raw, double green, double volume, double maturity)
        => Math.Min(4, Math.Max(0, volume) * Math.Clamp(raw + green, 0, 1)
            * (1 - .9 * Math.Clamp(maturity, 0, 1)));
}
