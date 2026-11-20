using System.Diagnostics;
using Seraph.Core.Framework;
using Seraph.World;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class PollutionRouteChecks
{
    internal static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var air = new Block { BlockId = 0, Code = new("game:air") };
        var stone = new Block { BlockId = 1, Code = new("game:stonebricks-granite"), BlockMaterial = EnumBlockMaterial.Stone };
        var water = new Block { BlockId = 2, Code = new("game:water-still-7"), MatterState = EnumMatterState.Liquid };
        var origin = new BlockPos(0, 8, 0, 0);
        var barrier = true;
        var reads = 0;
        var wet = false;
        var routes = new PollutionRouteCache(p => { reads++; return barrier && p.X == 1 ? stone : wet ? water : air; });
        void Settle()
        {
            for (var i = 0; i < 500; i++) { var budget = 128; var before = reads;
                routes.Advance(ref budget, Stopwatch.StartNew());
                Check(reads - before <= 128, "Path capture exceeded its budget."); }
        }
        routes.Request(origin, 0); Settle();
        Check(routes.Weight(origin, new(2, 7, 0, 0)) == 0
            && routes.Weight(origin, new(2, 8, 0, 0), true) == 0, "Pollution crossed a waterproof wall.");
        Check(routes.Weight(origin, new(0, 9, 0, 0)) == 0, "Runoff flowed uphill.");
        Check(routes.Weight(origin, new(0, 7, 0, 0)) > 0, "Downhill exposed waste failed to spread.");
        var beforeQuery = reads;
        var debug = routes.DebugCells(origin, true, true).ToArray();
        Check(debug.Any(p => p.Kind == PollutionDebugKind.Barrier && p.Position.X == 1)
            && debug.Any(p => p.Kind == PollutionDebugKind.WaterPath)
            && debug.Any(p => p.Kind == PollutionDebugKind.AirPath) && debug.Length <= 90,
            "Cached debug geometry lost walls, paths or its payload limit.");
        Check(!routes.DebugCells(origin, false, false).Any()
            && !routes.DebugCells(new(0, 8, 0, 1), true, true).Any(), "Debug paths escaped containment or dimension.");
        for (var i = 0; i < 1000; i++) routes.Weight(origin, new(0, 7, 0, 0));
        Check(beforeQuery == reads, "Inspection scanned the world.");
        barrier = false; routes.Invalidate(new(1, 8, 0, 0));
        Check(!routes.DebugCells(origin, true, true).Any(), "Invalidated barriers remained visible.");
        routes.Request(origin, 100); Settle();
        Check(!routes.DebugCells(origin, true, true).Any(p => p.Kind == PollutionDebugKind.Barrier),
            "Removed wall stayed in the debug cache.");
        Check(routes.Weight(origin, new(2, 7, 0, 0)) == 0, "Water travelled sideways through dry air.");
        wet = true; routes.Invalidate(origin); routes.Request(origin, 200); Settle();
        Check(routes.Weight(origin, new(2, 7, 0, 0)) > 0, "Removing a barrier failed to restore the flow path.");
        var unknown = new PollutionRouteCache(_ => null);
        unknown.Request(origin, 0);
        var unknownBudget = 128; unknown.Advance(ref unknownBudget, Stopwatch.StartNew());
        Check(!unknown.DebugCells(origin, true, true).Any(), "An unloaded cell appeared as a barrier or path.");
        Check(!PollutionTransport.Leaks(WasteContainment.OpenWatertight)
            && PollutionTransport.GasEscape(WasteContainment.OpenWatertight) == .6
            && PollutionTransport.GasEscape(WasteContainment.SealedWatertight) == 0
            && PollutionTransport.GasEscape(WasteContainment.ClosedStorage) == .25, "Containment routes disagree.");
        Check(PollutionTransport.WaterEscape(WasteContainment.Exposed) == 1
            && PollutionTransport.WaterEscape(WasteContainment.ClosedStorage) == .25
            && PollutionTransport.WaterEscape(WasteContainment.SealedWatertight) == 0, "Water escape is not independent of gas release.");
        foreach (var (kind, nh3, h2s) in new[] { (OrganicWasteKind.Fecal, .55, .15), (OrganicWasteKind.Corpse, .30, .35),
            (OrganicWasteKind.Rot, .20, .20), (OrganicWasteKind.ButcherRemains, .40, .25), (OrganicWasteKind.Compost, .25, .10) })
        {
            var gas = PollutionTransport.OrganicGas(kind, 1);
            Check(Math.Abs(gas.NH3 / .03 - nh3) < 1e-10 && Math.Abs(gas.H2S / .03 - h2s) < 1e-10, "Wrong organic gas signature: " + kind);
        }
        Check(PollutionTransport.VegetationRetention(32, 60, 2) < PollutionTransport.VegetationRetention(32, 60, 3)
            && PollutionTransport.VegetationRetention(32, 60, 3) < PollutionTransport.VegetationRetention(32, 60, 0)
            && PollutionTransport.VegetationRetention(32, 60, 0) < PollutionTransport.VegetationRetention(32, 60, 1)
            && PollutionTransport.VegetationRetention(0, 60, 2) == 1, "Gas-specific foliage retention is wrong.");
        using var changes = new PollutionChangeBridge();
        Check(PollutionVegetation.Classify(new Block { BlockId = 2, Code = new("game:log-grown-oak"), BlockMaterial = EnumBlockMaterial.Wood }).LeafUnits == 0,
            "A tree trunk counted as active foliage.");
        var leaf = new Block { BlockId = 3, Code = new("game:leaves-oak"), BlockMaterial = EnumBlockMaterial.Leaves };
        var reed = new Block { BlockId = 4, Code = new("game:waterplant-reed"), BlockMaterial = EnumBlockMaterial.Plant };
        var divider = true;
        var plants = new PollutionPlantCache(p => { reads++; return p.X == 1 && divider ? stone : p.X == 2 ? leaf : air; });
        void PlantsSettle(PollutionPlantCache cache)
        {
            for (var i = 0; i < 100; i++) {
                var before = reads; cache.Advance(0, 16, Stopwatch.StartNew());
                Check(reads - before <= 16, "Plant survey exceeded its read budget.");
            }
        }
        plants.At(origin, 0); PlantsSettle(plants);
        Check(plants.At(origin, 1).Active == 0, "Foliage purified air through a wall.");
        divider = false; plants.Invalidate(new(1, 8, 0, 0)); plants.At(origin, 2); PlantsSettle(plants);
        Check(plants.At(origin, 3).Active > 0, "Connected foliage did not contact gas.");
        var beforePlantQueries = reads;
        for (var i = 0; i < 100; i++) plants.At(origin, 4);
        Check(reads == beforePlantQueries, "Gas queries scanned plants synchronously.");
        plants.At(origin, 20000);
        Check(plants.At(origin, 20001).Active > 0, "Refreshing a survey discarded its last complete result.");
        var aquatic = new PollutionPlantCache(p => p.X == 2 ? reed : p.X == 1 ? stone : water);
        aquatic.At(origin, 0, true); PlantsSettle(aquatic);
        Check(aquatic.At(origin, 1, true).Aquatic == 0, "Water plants filtered through a stone divider.");
        Check(PollutionTransport.RiparianRetention(1000) == .7
            && PollutionTransport.VegetationRetention(32, 60, true) == .9
            && PollutionTransport.AquaticRetention(16, 48) == .5, "Plant retention exceeded its calibrated limits.");
        Console.WriteLine("PASS pollution containment, loaded path budgets, walls, uphill protection, barrier removal and cached inspection");
    }
}
