using Seraph.Client;
using Seraph.Network;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class RuntimeWorkChecks
{
    internal static void Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var region = new BodyRegionSnapshot { Region = "torso", Integrity = 15 };
        var snapshot = new ServerSnapshot { BodyRegions = [region] };
        var key = BodyRenderer.VisualKey(snapshot);
        snapshot.Sequence++;
        Check(key == BodyRenderer.VisualKey(snapshot), "Unchanged body pixels requested another texture upload.");
        region.Integrity = 14.99;
        Check(key != BodyRenderer.VisualKey(snapshot), "First impairment failed to change color stage.");
        key = BodyRenderer.VisualKey(snapshot); region.Integrity = 14;
        Check(key == BodyRenderer.VisualKey(snapshot), "Same color stage requested another texture upload.");
        region.Wound = true; region.Integrity = 14.01;
        Check(key == BodyRenderer.VisualKey(snapshot), "Unchanged wound stage requested another texture upload.");
        region.Integrity = 7.5;
        Check(key != BodyRenderer.VisualKey(snapshot), "Color threshold failed to redraw.");
        Check(BodyRenderer.VisualKey(null) != BodyRenderer.VisualKey(snapshot), "Stale body state failed to redraw.");

        var cursor = 0; var reads = 0; var visits = new HashSet<BlockPos>();
        var loaded = true; var surface = 100;
        var air = new Block { BlockId = 0 };
        var chunk = (IWorldChunk)RoutingProxy.Make(typeof(IWorldChunk), (_, _) => null);
        var blocks = (IBlockAccessor)RoutingProxy.Make(typeof(IBlockAccessor), (m, _) => m.Name switch {
            "GetChunkAtBlockPos" => loaded ? chunk : null,
            "GetTerrainMapheightAt" => surface,
            "GetBlock" => Read(), _ => throw new Exception(m.Name)
        });
        Block Read() { reads++; return air; }
        var origin = new BlockPos(10, 20, 30, 0);
        for (var i = 0; i < 4; i++)
        {
            var before = reads;
            GroundwaterExposureScan.Visit(blocks, origin, ref cursor, p => visits.Add(p));
            Check(reads - before <= GroundwaterExposureScan.CellsPerPass, "Groundwater scan exceeded per-callback budget.");
        }
        Check(visits.Count == 343, "Distributed sweep missed an excavation cell.");
        surface = 0; reads = 0; visits.Clear();
        GroundwaterExposureScan.Visit(blocks, origin, ref cursor, p => visits.Add(p));
        Check(reads == 0 && visits.Count == 0, "Above-ground air still queried groundwater.");
        loaded = false; surface = 100;
        GroundwaterExposureScan.Visit(blocks, origin, ref cursor, p => visits.Add(p));
        Check(reads == 0 && visits.Count == 0, "Background scan accessed an unloaded chunk.");
        Console.WriteLine("PASS body redraw changes and bounded groundwater exposure sweep, above-ground and unloaded skips");
    }
}
