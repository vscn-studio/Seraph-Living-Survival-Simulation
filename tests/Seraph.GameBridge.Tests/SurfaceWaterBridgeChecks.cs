using System.Reflection;
using Seraph;
using Seraph.World;
using Seraph.Application;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class SurfaceWaterBridgeChecks
{
    internal static void Run()
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var type = typeof(SeraphModSystem).Assembly.GetType("Seraph.World.SurfaceWaterClassifier", true)!;
        const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var air = new Block { BlockId = 0, Code = new("game:air") };
        var water = new BlockWater { BlockId = 1, Code = new("game:water-still-7"), LiquidCode = "water" };
        foreach (var scenario in new[] { "pond", "lake", "groundwater", "missing", "river", "river-bay" }) {
            var y = scenario == "groundwater" ? 10 : 64;
            var width = scenario == "lake" ? 52 : 8;
            var climateReads = 0;
            ClimateCondition Climate() { climateReads++; return new() { Temperature = 15, Rainfall = .5f }; }
            var calendar = Proxy<IGameCalendar>((m, _) => m.Name switch { "get_TotalHours" => 10d, "get_HoursPerDay" => 24f, _ => null });
            var chunk = Proxy<IWorldChunk>((m, _) => null);
            var riverMap = new ushort[1024];
            if (scenario == "river-bay") { Array.Fill(riverMap, (ushort)1); riverMap[3 * 32 + 3] = 0; }
            var map = Proxy<IMapChunk>((m, _) => m.Name == "GetModdata" ? riverMap : null);
            var blocks = Proxy<IBlockAccessor>((m, a) => {
                var pos = a?.FirstOrDefault() as BlockPos;
                return m.Name switch {
                    "get_MapSizeY" => 256,
                    "GetChunkAtBlockPos" => scenario == "missing" && pos!.X > 3 ? null : chunk,
                    "GetMapChunkAtBlockPos" => map,
                    "GetRainMapHeightAt" => 64,
                    "GetClimateAt" => Climate(),
                    "GetBlock" => pos!.Y == y && (pos.X >= 0 && pos.X < width || scenario == "pond" && pos.X >= 100 && pos.X < 108) && pos.Z >= 0 && pos.Z < width ? water : air,
                    _ => throw new Exception("Unexpected classifier block call: " + m.Name)
                };
            });
            var loader = Proxy<IModLoader>((m, _) => m.Name == "IsModEnabled" && scenario is "river" or "river-bay");
            var events = Proxy<IServerEventAPI>((m, _) => m.Name == "RegisterGameTickListener" ? 1L : null);
            var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
                "get_BlockAccessor" => blocks, "get_ElapsedMilliseconds" => 1000L, "get_SeaLevel" => 64,
                "get_Calendar" => calendar, "get_Seed" => 42,
                _ => throw new Exception("Unexpected classifier world call: " + m.Name)
            });
            var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch { "get_World" => world, "get_Event" => events, "get_ModLoader" => loader, _ => null });
            using var classifier = (IDisposable)Activator.CreateInstance(type, all, null, [api], null)!;
            var query = type.GetMethod("Query", all)!;
            var tick = type.GetMethod("Tick", all)!;
            var pos = new BlockPos(1, y, 1, 0);
            query.Invoke(classifier, [pos]);
            if (scenario == "lake")
            {
                var revision = type.GetProperty("Revision", all)!.GetValue(classifier);
                type.GetMethod("InvalidateAt", all)!.Invoke(classifier, [new BlockPos(500, y, 500, 0)]);
                if (!Equals(revision, type.GetProperty("Revision", all)!.GetValue(classifier)))
                    throw new Exception("Unrelated block edits restarted pending water classification.");
                for (var x = 2; x < 8; x++) query.Invoke(classifier, [new BlockPos(x, y, 1, 0)]);
            }
            for (var i = 0; i < 1000; i++) tick.Invoke(classifier, [.05f]);
            var result = (string)query.Invoke(classifier, [pos])!;
            var expected = scenario == "missing" ? "surface" : scenario == "river-bay" ? "river" : scenario;
            if (result != expected) throw new Exception($"{scenario} classified as {result}, expected {expected}");
            if ((((SurfaceWaterClassifier)classifier).PondAt(pos) is not null) != (scenario == "pond"))
                throw new Exception($"{scenario} incorrectly included in closed-pond transport.");
            if (scenario == "lake" && (long)type.GetProperty("BlockReads", all)!.GetValue(classifier)! > 16000)
                throw new Exception("Sweeping connected water restarted duplicate lake scans.");
            if (scenario == "pond")
            {
                WaterQuality.BindClassifier(world, (SurfaceWaterClassifier)classifier);
                var config = new SeraphServerConfig();
                var first = WaterQuality.WorldSource(world, pos, config);
                var before = climateReads;
                var revision = ((SurfaceWaterClassifier)classifier).Revision;
                query.Invoke(classifier, [new BlockPos(101, y, 1, 0)]);
                for (var i = 0; i < 100; i++) tick.Invoke(classifier, [.05f]);
                if (((SurfaceWaterClassifier)classifier).Revision <= revision
                    || !ReferenceEquals(first, WaterQuality.WorldSource(world, pos, config)) || climateReads != before)
                    throw new Exception("An unrelated water body discarded cached quality and repeated climate sampling.");
            }
        }
        Console.WriteLine("PASS bounded water topology: pond, lake, underground, unloaded boundary and Rivers column");
    }
}
