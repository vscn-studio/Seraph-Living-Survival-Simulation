using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class PondPollutionChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var storeType = typeof(ChunkPollutionStore);
        object? Field(object o, string name) => o.GetType().GetField(name, flags)!.GetValue(o);
        void Set(object o, string name, object value) => o.GetType().GetField(name, flags)!.SetValue(o, value);
        object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, flags)!.Invoke(o, args);
        var columnType = storeType.GetNestedType("Column", flags)!;
        var sourceType = storeType.GetNestedType("LocalSource", flags)!;
        var residueType = storeType.GetNestedType("Residue", flags)!;
        var air = new Block { BlockId = 0, Code = new("game:air") };
        var water = new BlockWater { BlockId = 1, Code = new("game:water-still-7"), LiquidCode = "water", MatterState = EnumMatterState.Liquid };
        var wall = new Block { BlockId = 2, Code = new("game:stonebricks-granite"), BlockMaterial = EnumBlockMaterial.Stone };
        var hours = 10d; var now = 1000L; var reads = 0; var divided = false; var drained = false;
        var unloaded = false;
        bool IsWet(BlockPos p) => !drained && p.Y == 64 && p.X is >= 28 and < 44 && p.Z is >= 0 and < 4 && !(divided && p.X == 35);
        Block BlockAt(BlockPos p) => IsWet(p) ? water : p.Y < 64 || divided && p.X == 35 ? wall : air;
        var saved = new[] { new Dictionary<string,byte[]>(), new Dictionary<string,byte[]>() };
        var chunks = new IWorldChunk[2];
        for (var x = 0; x < 2; x++)
        {
            var cx = x;
            var data = Proxy<IChunkBlocks>((m, a) => {
                if (m.Name is "GetBlockId" or "GetFluid")
                {
                    reads++;
                    var index = (int)a![0]!;
                    var p = new BlockPos(cx * 32 + (index & 31), 64 + (index >> 10), (index >> 5) & 31, 0);
                    var b = BlockAt(p);
                    return m.Name == "GetFluid" ? b == water ? 1 : 0 : b == wall ? 2 : 0;
                }
                throw new Exception("Pond data " + m.Name);
            });
            chunks[x] = Proxy<IWorldChunk>((m, a) => m.Name switch {
                "get_Disposed" => false, "get_Data" => data, "Unpack_ReadOnly" => true,
                "GetModdata" => saved[cx].GetValueOrDefault((string)a![0]!),
                "SetModdata" => Save(cx, (string)a![0]!, (byte[])a[1]!), "MarkModified" => null,
                _ => throw new Exception("Pond chunk " + m.Name) });
        }
        object? Save(int x, string key, byte[] bytes) { saved[x][key] = bytes; return null; }
        var blocks = Proxy<IBlockAccessor>((m, a) => {
            var p = a?.FirstOrDefault() as BlockPos;
            return m.Name switch {
                "get_MapSizeY" => 256, "GetChunkAtBlockPos" => unloaded && p!.X >= 32 ? null : chunks[p!.X < 32 ? 0 : 1],
                "GetBlock" => BlockAt(p!), "GetRainMapHeightAt" => 64,
                "GetClimateAt" => new ClimateCondition { Temperature = 15, Rainfall = .5f },
                _ => throw new Exception("Pond accessor " + m.Name) };
        });
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name switch { "get_TotalHours" => hours, "get_HoursPerDay" => 24f, _ => null });
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_BlockAccessor" => blocks, "get_Calendar" => calendar, "get_ElapsedMilliseconds" => now,
            "get_SeaLevel" => 64, "get_Blocks" => new List<Block> { air, water, wall },
            _ => throw new Exception("Pond world " + m.Name) });
        var events = Proxy<IServerEventAPI>((m, _) => m.Name == "RegisterGameTickListener" ? 1L : null);
        var loader = Proxy<IModLoader>((m, _) => m.Name == "IsModEnabled" ? false : null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Event" => events, "get_ModLoader" => loader,
            "get_Logger" => Proxy<ILogger>((_, _) => null), _ => null });
        using var classifier = new SurfaceWaterClassifier(api);
        using var store = new ChunkPollutionStore(api, () => new SeraphServerConfig());
        WaterQuality.BindClassifier(world, classifier); WaterQuality.BindPollution(world, store);
        var columns = (IDictionary)Field(store, "columns")!;
        object NewColumn(int x)
        {
            var c = Activator.CreateInstance(columnType, true)!;
            ((IDictionary)Field(c, "Chunks")!)[2] = chunks[x];
            Set(c, "Region", new RegionalEnvironment());
            Set(c, "Heights", Enumerable.Repeat((ushort)64, 1024).ToArray());
            columns[new PollutionChunk(x, 0, 0)] = c;
            return c;
        }
        var leftColumn = NewColumn(0); var rightColumn = NewColumn(1);
        var origin = new BlockPos(28, 65, 1, 0);
        var source = Activator.CreateInstance(sourceType, [28, 65, 1, 64, new PollutionLoad(1, 0, 0, 0, 0),
            new Dictionary<string,double> { ["giardia"] = .2 }, true, 1d, 1d, OrganicWasteKind.Fecal, 1d, 0L])!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType))!;
        list.Add(source);
        ((IDictionary)Field(leftColumn, "Points")!)[(7, 0)] = list;
        ((IDictionary)Field(leftColumn, "Residues")!)[(28, 65, 1)] = Activator.CreateInstance(residueType, [source,
            new RegionalEnvironment { SurfacePathogens = .8, SurfaceAgents = new Dictionary<string,double> { ["giardia"] = .2 } }])!;
        var routes = (PollutionRouteCache)Field(store, "routes")!;
        routes.Request(origin, now);
        for (var i = 0; i < 1000; i++) { var budget = 128; routes.Advance(ref budget, Stopwatch.StartNew()); }
        void Classify(BlockPos p)
        {
            classifier.Query(p);
            for (var i = 0; i < 1000; i++) Call(classifier, "Tick", .05f);
        }
        var near = new BlockPos(28, 64, 1, 0); var far = new BlockPos(43, 64, 1, 0);
        Classify(near);
        Check(classifier.PondAt(near)?.Cells.Count == 64 && ReferenceEquals(classifier.PondAt(near), classifier.PondAt(far)),
            "A cross-chunk closed pond did not share one complete topology.");
        void Pump()
        {
            for (var i = 0; i < 200; i++)
            {
                var before = reads;
                Call(store, "AdvancePonds");
                Check(reads - before <= 256, "Pond maintenance exceeded its independent read budget.");
            }
        }
        Pump();
        hours += .25; Pump();
        var sourceSites = (IDictionary)Field(leftColumn, "Residues")!;
        var transferredState = (RegionalEnvironment)sourceSites[(28, 65, 1)]!.GetType().GetProperty("State")!.GetValue(sourceSites[(28, 65, 1)])!;
        Check(transferredState.SurfacePathogens < .8 && store.WaterResidueAt(near, false).Generic > 0,
            "Pond transfer copied source stock instead of consuming it.");
        Pump();
        Check(ReferenceEquals(transferredState, sourceSites[(28, 65, 1)]!.GetType().GetProperty("State")!.GetValue(sourceSites[(28, 65, 1)]))
                || Math.Abs(((RegionalEnvironment)sourceSites[(28, 65, 1)]!.GetType().GetProperty("State")!.GetValue(sourceSites[(28, 65, 1)])!).SurfacePathogens
                    - transferredState.SurfacePathogens) < 1e-12,
            "Repeated observations discharged the same source without elapsed game time.");
        for (var step = 0; step < 48; step++)
        {
            hours += .25;
            var sites = (IDictionary)Field(leftColumn, "Residues")!;
            var current = sites[(28, 65, 1)]!;
            var state = (RegionalEnvironment)current.GetType().GetProperty("State")!.GetValue(current)!;
            var next = RegionalEnvironmentModel.Regional(state, new RegionalSources { Fecal = 1,
                Pathogens = new Dictionary<string,double> { ["giardia"] = .2 } }, .25, .5, 0);
            var nextSite = Activator.CreateInstance(residueType, [source, next])!;
            nextSite.GetType().GetProperty("PondTransferHour")!.SetValue(nextSite,
                current.GetType().GetProperty("PondTransferHour")!.GetValue(current));
            sites[(28, 65, 1)] = nextSite;
            Pump();
        }
        var nearLoad = store.WaterResidueAt(near, false); var farLoad = store.WaterResidueAt(far, false);
        Check(farLoad.Generic > 0 && farLoad.Agents.GetValueOrDefault("giardia") > 0 && nearLoad.Generic > farLoad.Generic,
            "Contamination did not mix beyond the local source route, lost species or mixed instantly.");
        var beforeRead = reads;
        for (var i = 0; i < 100; i++) store.WaterResidueAt(far, false);
        Check(reads == beforeRead, "Pond inspection synchronously read world blocks.");
        store.Save();
        var total = saved.Sum(s => s["vsslssmod:pond-cells-v1"].Length);
        Check(total > 0, "Pond mass was not persisted across both chunks.");
        // Reload each column from actual chunk moddata; no second source ledger is injected.
        leftColumn = NewColumn(0); rightColumn = NewColumn(1);
        Set(leftColumn, "Region", Call(store, "Restore", leftColumn)!);
        Set(rightColumn, "Region", Call(store, "Restore", rightColumn)!);
        var restored = store.WaterResidueAt(far, false);
        Check(Math.Abs(restored.Generic - farLoad.Generic) < 1e-10 && Math.Abs(restored.Agents["giardia"] - farLoad.Agents["giardia"]) < 1e-10,
            "Cross-chunk restart duplicated or lost pond mass.");
        hours += 12; Pump();
        Check(store.WaterResidueAt(far, false).Generic < farLoad.Generic && store.WaterResidueAt(far, false).Generic > 0,
            "Removing the source erased the pond or failed to decay its residue.");
        divided = true; classifier.InvalidateAt(new(35, 64, 1, 0)); Classify(near); Classify(far);
        Check(!ReferenceEquals(classifier.PondAt(near), classifier.PondAt(far)), "A new solid divider did not split the pond.");
        Pump();
        var isolated = store.WaterResidueAt(far, false).Generic;
        hours += 1; Pump();
        Check(store.WaterResidueAt(far, false).Generic <= isolated, "A split pond manufactured pollution.");
        unloaded = true; columns.Remove(new PollutionChunk(1, 0, 0)); classifier.Invalidate(); Classify(near);
        Check(classifier.PondAt(near) is null, "Unloaded topology was treated as a closed pond.");
        drained = true; Pump();
        Check(((IDictionary)Field(leftColumn, "PondCells")!).Count == 0, "Drained water retained a transferable pond stock.");
        Console.WriteLine("PASS pond pollution: shared cross-chunk water, gradual mixing, bounded work, cache-only sampling, save/reload, source removal, split, unload and drain");
    }
}
