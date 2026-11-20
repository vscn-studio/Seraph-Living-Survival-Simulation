using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.World;

/// <summary>Loaded-only contact surveys. Queries enqueue work; they never scan terrain.</summary>
internal sealed class PollutionPlantCache(System.Func<BlockPos, Block?> read)
{
    internal readonly record struct Contact(double Active, double Soot, double Aquatic);
    private readonly record struct Key(int X, int Y, int Z, int D, bool Water)
    { internal BlockPos Pos => new(X, Y, Z, D); }
    private sealed class Job(Key origin, long at)
    {
        internal readonly Key Origin = origin;
        internal readonly long At = at;
        internal readonly Queue<Key> Pending = new([origin]);
        internal readonly HashSet<Key> Seen = [origin];
        internal double Active, Soot, Aquatic;
        internal bool Complete;
        internal Contact Previous;
    }
    private readonly Dictionary<Key, Job> cache = [];
    private readonly Dictionary<Block, PollutionPlant> types = [];
    private readonly Queue<Job> jobs = [];
    internal Contact At(BlockPos pos, long now, bool water = false)
    {
        var key = new Key(pos.X, pos.Y, pos.Z, pos.dimension, water);
        cache.TryGetValue(key, out var job);
        var result = job is { Complete: true } ? new Contact(job.Active, job.Soot, job.Aquatic) : job?.Previous ?? default;
        if (job is null || job.Complete && now - job.At > 10000)
        {
            if (jobs.Count >= 128) return result;
            if (cache.Count >= 2048 && job is null) cache.Remove(cache.MinBy(p => p.Value.At).Key);
            cache[key] = job = new(key, now) { Previous = result }; jobs.Enqueue(job);
        }
        return result;
    }
    internal void Advance(long now, int budget, Stopwatch watch)
    {
        while (budget-- > 0 && jobs.TryPeek(out var job) && watch.Elapsed.TotalMilliseconds < .5)
        {
            if (!cache.TryGetValue(job.Origin, out var live) || !ReferenceEquals(job, live)) { jobs.Dequeue(); continue; }
            if (!job.Pending.TryDequeue(out var cell)) { job.Complete = true; jobs.Dequeue(); continue; }
            var block = read(cell.Pos);
            if (block is null) continue;
            if (!types.TryGetValue(block, out var plant)) types[block] = plant = PollutionVegetation.Classify(block);
            job.Active = Math.Min(32, job.Active + plant.ActiveUnits);
            job.Soot = Math.Min(32, job.Soot + plant.SootUnits);
            if (plant.Aquatic && !plant.Dormant) job.Aquatic = Math.Min(16, job.Aquatic + plant.ActiveUnits * 4);
            var origin = cell == job.Origin;
            var pass = job.Origin.Water ? block.IsLiquid() || plant.Aquatic : block.Id == 0
                || plant.LeafUnits > 0 || block.IsLiquid();
            if (!pass && !origin) continue;
            foreach (var facing in BlockFacing.ALLFACES)
            {
                var next = cell with { X = cell.X + facing.Normali.X, Y = cell.Y + facing.Normali.Y, Z = cell.Z + facing.Normali.Z };
                var radius = job.Origin.Water ? 4 : 2;
                if (Math.Abs(next.X - job.Origin.X) > radius || Math.Abs(next.Z - job.Origin.Z) > radius
                    || Math.Abs(next.Y - job.Origin.Y) > (job.Origin.Water ? 1 : 2) || job.Seen.Count >= 256) continue;
                // Keep aquatic counts in one fixed 8×8 patch, preventing a dense
                // connected reed bed from giving unlimited treatment at its edge.
                if (job.Origin.Water && ((next.X >> 3) != (job.Origin.X >> 3) || (next.Z >> 3) != (job.Origin.Z >> 3))) continue;
                if (job.Seen.Add(next)) job.Pending.Enqueue(next);
            }
        }
    }
    internal void Invalidate(BlockPos pos)
    {
        foreach (var key in cache.Keys.Where(p => p.D == pos.dimension && Math.Abs(p.X - pos.X) <= 4
            && Math.Abs(p.Y - pos.Y) <= 2 && Math.Abs(p.Z - pos.Z) <= 4).ToArray()) cache.Remove(key);
    }
}
