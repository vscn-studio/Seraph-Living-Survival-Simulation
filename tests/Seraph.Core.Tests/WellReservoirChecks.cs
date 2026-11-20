using Seraph.Core.Framework;

internal static class WellReservoirChecks
{
    internal static void Storage()
    {
        void Check(bool condition) { if (!condition) throw new Exception("Well reservoir geometry/conservation failed."); }
        Check(WellReservoir.Capacity(100, 10, 120, 21) == 0);
        var capacity = WellReservoir.Capacity(100, 10, 120, 18);
        Check(capacity == 2 * WellReservoir.AreaM2 * 1000);
        Check(WellReservoir.Capacity(100, 10, 120, 0) == 10 * WellReservoir.AreaM2 * 1000);
        Check(WellReservoir.Capacity(100, 20, 120, 18) == capacity);
        Check(WellReservoir.Height(capacity) == 2);
        foreach (var material in Enum.GetValues<AquiferMaterialClass>())
        {
            var rate = WellReservoir.InflowPerDay(capacity, 0, material);
            Check(rate is >= 4.8 and <= 21.6);
            Check(WellReservoir.InflowPerDay(1000000, 0, material) <= 21.6);
            Check(WellReservoir.InflowPerDay(capacity, capacity - 10, material) < rate);
            Check(WellReservoir.InflowPerDay(0, 0, material) == 0);
            Check(WellReservoir.InflowPerDay(double.NaN, 0, material) == 0);
            // Even the best deep well needs over eleven game hours for a 10 L bucket.
            Check(10 / WellReservoir.InflowPerDay(1000000, 0, material) * 24 >= 11);
            Check(rate > 0 && WellReservoir.InflowPerDay(capacity, capacity, material) == 0);
            Check(WellReservoir.Incoming(capacity, 0, rate, 0, 1e6) == 0);
            Check(WellReservoir.Incoming(capacity, 0, rate, .0001, 1e6) < capacity);
            Check(WellReservoir.Incoming(capacity, capacity - 1, rate, 1, 1e6) <= 1);
            Check(WellReservoir.Incoming(capacity, 0, rate, 1, .01) <= .01);
            var stored = 0d;
            var aquifer = 2 * capacity;
            for (var i = 0; i < 100; i++)
            {
                var inflow = WellReservoir.Incoming(capacity, stored,
                    WellReservoir.InflowPerDay(capacity, stored, material), .001, aquifer);
                aquifer -= inflow; stored += inflow;
                Check(Math.Abs(aquifer + stored - 2 * capacity) < 1e-8 && stored <= capacity);
            }
        }
    }
}
