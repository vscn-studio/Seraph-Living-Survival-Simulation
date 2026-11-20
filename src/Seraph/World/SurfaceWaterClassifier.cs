using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.World;

/// <summary>Bounded loaded-only topology, including complete small connected water bodies.</summary>
internal sealed class SurfaceWaterClassifier : IDisposable
{
    internal sealed class Pond(IReadOnlyList<BlockPos> cells)
    {
        internal readonly IReadOnlyList<BlockPos> Cells = cells;
    }
    private readonly ICoreServerAPI api;
    private readonly SlowCallbackProbe tickProbe = new("server.water-classifier.tick", 50);
    private readonly long listener;
    private readonly bool rivers;
    private readonly System.Reflection.MethodInfo? streamQuery;
    private readonly Dictionary<BlockPos, (string Kind, long At)> cache = new();
    private readonly Dictionary<BlockPos, Pond> ponds = new();
    internal Pond? PondAt(BlockPos pos) => cache.TryGetValue(pos, out var entry) && entry.Kind == "pond"
        && api.World.ElapsedMilliseconds - entry.At is >= 0 and < 30000 ? ponds.GetValueOrDefault(pos) : null;
    internal IEnumerable<Pond> Ponds => ponds.Values.Distinct().Where(p => PondAt(p.Cells[0]) == p);
    private readonly Queue<Job> jobs = new();
    private readonly HashSet<BlockPos> pending = new();
    internal long Revision { get; private set; }
    internal long BlockReads { get; private set; }
    private sealed class Job(BlockPos start)
    {
        internal readonly BlockPos Start = start;
        internal readonly Queue<BlockPos> Queue = new([start]);
        internal readonly HashSet<BlockPos> Seen = new() { start };
        internal readonly List<BlockPos> Water = [];
        internal bool Surface, Missing, River;
    }
    internal SurfaceWaterClassifier(ICoreServerAPI api)
    {
        this.api = api;
        rivers = api.ModLoader.IsModEnabled("rivers");
        if (api.ModLoader.IsModEnabled("watersheds"))
            streamQuery = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Watersheds.WorldGen.Water.StreamWaterCache"))
                .FirstOrDefault(t => t is not null)?.GetMethod("TryGet", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        listener = api.Event.RegisterGameTickListener(Tick, 50);
    }
    internal string Query(BlockPos pos)
    {
        if (IsMappedRiver(pos)) return "river";
        var now = api.World.ElapsedMilliseconds;
        if (cache.TryGetValue(pos, out var entry) && now - entry.At is >= 0 and < 30000) return entry.Kind;
        // A sweep across a connected body must not enqueue a second flood fill
        // for cells already reached by the active scan.
        if (!jobs.Any(job => job.Seen.Contains(pos)) && jobs.Count < 16 && pending.Add(pos.Copy())) jobs.Enqueue(new(pos.Copy()));
        return "surface";
    }
    private bool IsMappedRiver(BlockPos pos)
    {
        if (streamQuery is not null && pos.dimension == 0)
        {
            try {
                object?[] args = [api.World.BlockAccessor, pos.X, pos.Y, pos.Z, null, 0f];
                if (streamQuery.Invoke(null, args) is true) return true;
            } catch (System.Reflection.TargetInvocationException) { }
        }
        return rivers && IsRiver(pos);
    }
    internal void InvalidateAt(BlockPos changed)
    {
        if (jobs.Any(job => job.Seen.Contains(changed)) || cache.Keys.Any(p => p.dimension == changed.dimension && Math.Abs(p.X - changed.X) <= 1
            && Math.Abs(p.Y - changed.Y) <= 1 && Math.Abs(p.Z - changed.Z) <= 1)) Invalidate();
    }
    internal void Invalidate()
    {
        cache.Clear(); ponds.Clear(); jobs.Clear(); pending.Clear(); Revision++;
    }
    private bool IsRiver(BlockPos pos)
    {
        // Rivers currently publishes its column map in the overworld only.
        if (pos.dimension != 0) return false;
        try {
            var size = Vintagestory.API.Config.GlobalConstants.ChunkSize;
            var index = ((pos.Z % size + size) % size) * size + (pos.X % size + size) % size;
            var distance = api.World.BlockAccessor.GetMapChunkAtBlockPos(pos)?.GetModdata<ushort[]>("riverDistance");
            if (distance is { } && distance.Length == size * size) return distance[index] == 0;
            var flow = api.World.BlockAccessor.GetChunkAtBlockPos(new(pos.X, 0, pos.Z, pos.dimension))?.GetModdata<float[]>("flowVectors");
            return flow?.Length == size * size * 2 && float.IsFinite(flow[index * 2]) && float.IsFinite(flow[index * 2 + 1])
                && (Math.Abs(flow[index * 2]) > .00001 || Math.Abs(flow[index * 2 + 1]) > .00001);
        } catch { return false; }
    }
    private void Tick(float dt)
    {
        if (jobs.Count == 0) return;
        using var timing = tickProbe.Measure(api);
        var watch = Stopwatch.StartNew();
        var job = jobs.Peek();
        if (cache.TryGetValue(job.Start, out var known) && known.Kind != "surface"
            && api.World.ElapsedMilliseconds - known.At is >= 0 and < 30000)
        { jobs.Dequeue(); pending.Remove(job.Start); return; }
        var blocks = api.World.BlockAccessor;
        var reads = 0;
        while (job.Queue.Count > 0 && job.Water.Count < 4000 && reads < 256 && watch.Elapsed.TotalMilliseconds < 1)
        {
            var pos = job.Queue.Dequeue();
            reads++; BlockReads++;
            if (pos.Y < 0 || pos.Y >= blocks.MapSizeY) continue;
            if (blocks.GetChunkAtBlockPos(pos) is null) { job.Missing = true; continue; }
            if (!WaterInteractionTarget.TryGetWaterAt(blocks, pos, out var fluid, out _)
                || fluid.LiquidCode == "saltwater") continue;
            job.Water.Add(pos);
            job.River |= IsMappedRiver(pos);
            job.Surface |= blocks.GetRainMapHeightAt(pos) <= pos.Y + 1;
            foreach (var face in BlockFacing.ALLFACES) {
                var next = pos.AddCopy(face);
                if (job.Seen.Add(next)) job.Queue.Enqueue(next);
            }
            if (job.River || job.Surface && job.Water.Count > 2000) break;
        }
        var lake = job.Surface && job.Water.Count > 2000;
        var finished = job.Queue.Count == 0 || job.Water.Count >= 4000 || lake || job.River;
        // Finish one bounded scan before servicing duplicates in the same body.
        if (!finished) return;
        jobs.Dequeue();
        pending.Remove(job.Start);
        var certain = job.River || lake || (!job.Missing && job.Queue.Count == 0);
        var kind = job.River ? "river" : !certain ? "surface" : !job.Surface && job.Start.Y < api.World.SeaLevel - 4
            ? "groundwater" : job.Water.Count > 2000 ? "lake" : "pond";
        // Unknown results retry soon. A partially loaded body is never labelled a pond.
        var at = api.World.ElapsedMilliseconds - (certain ? 0 : 25000);
        if (cache.Count + job.Water.Count > 16384) { cache.Clear(); ponds.Clear(); }
        var pond = kind == "pond" && job.Water.Count > 0 ? new Pond(job.Water.ToArray()) : null;
        foreach (var pos in job.Water) {
            cache[pos] = (kind, at);
            ponds.Remove(pos);
            if (pond is not null) ponds[pos] = pond;
        }
        cache[job.Start] = (kind, at);
        Revision++;
    }
    public void Dispose() { api.Event.UnregisterGameTickListener(listener); Invalidate(); }
}
