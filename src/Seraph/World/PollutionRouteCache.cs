using System.Diagnostics;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.World;

/// <summary>Loaded-only, bounded transport paths. Inspection only reads completed caches.</summary>
internal sealed class PollutionRouteCache(System.Func<BlockPos, Block?> read)
{
    private readonly record struct Cell(int X, int Y, int Z, int D)
    {
        internal BlockPos Pos => new(X, Y, Z, D);
    }
    private sealed class Route(Cell origin, long now)
    {
        internal readonly Cell Origin = origin;
        internal readonly long At = now;
        internal readonly Dictionary<Cell, double> Water = new() { [origin] = 1 };
        internal readonly Dictionary<Cell, double> Air = new() { [origin] = 1 };
        // Retain only real loaded solid barriers encountered by the existing survey.
        // Bits distinguish water and air blockage; unloaded cells are never walls.
        internal readonly Dictionary<Cell, byte> Barriers = new();
        internal readonly PriorityQueue<(Cell Cell, double Weight, bool Air), double> Pending = new();
        internal bool Complete;
    }
    private readonly Dictionary<Cell, Route> routes = new();
    private readonly Queue<Route> jobs = new();
    internal int Reads { get; private set; }
    private static Cell Key(BlockPos p) => new(p.X, p.Y, p.Z, p.dimension);

    internal void Request(BlockPos origin, long now)
    {
        var key = Key(origin);
        if (routes.TryGetValue(key, out var previous) && now - previous.At < 60000) return;
        if (jobs.Count >= 256) return;
        if (routes.Count >= 256 && !routes.ContainsKey(key))
            routes.Remove(routes.MinBy(p => p.Value.At).Key);
        var route = new Route(key, now);
        route.Pending.Enqueue((key, 1, false), -1);
        route.Pending.Enqueue((key, 1, true), -1);
        routes[key] = route; jobs.Enqueue(route);
    }

    internal double Weight(BlockPos origin, BlockPos target, bool air = false)
        => routes.TryGetValue(Key(origin), out var route)
            ? (air ? route.Air : route.Water).GetValueOrDefault(Key(target)) : 0;

    internal IEnumerable<(BlockPos Position, double Weight)> WaterCells(BlockPos origin)
        => routes.TryGetValue(Key(origin), out var route) && route.Complete
            ? route.Water.Select(p => (p.Key.Pos, p.Value)) : [];

    internal IEnumerable<(BlockPos Position, PollutionDebugKind Kind, double Strength)> DebugCells(
        BlockPos origin, bool water, bool air)
    {
        if (!routes.TryGetValue(Key(origin), out var route)) yield break;
        int Distance(Cell c) => Math.Abs(c.X - origin.X) + Math.Abs(c.Y - origin.Y) + Math.Abs(c.Z - origin.Z);
        if (water)
            foreach (var p in route.Water.Where(p => p.Key != route.Origin).OrderBy(p => Distance(p.Key)).Take(32))
                yield return (p.Key.Pos, PollutionDebugKind.WaterPath, p.Value);
        if (air)
            foreach (var p in route.Air.Where(p => p.Key != route.Origin).OrderBy(p => Distance(p.Key)).Take(32))
                yield return (p.Key.Pos, PollutionDebugKind.AirPath, p.Value);
        foreach (var p in route.Barriers.Where(p => (p.Value & ((water ? 1 : 0) | (air ? 2 : 0))) != 0)
            .OrderBy(p => Distance(p.Key)).Take(26))
            yield return (p.Key.Pos, PollutionDebugKind.Barrier, 1);
    }

    internal void Invalidate(BlockPos changed)
    {
        foreach (var key in routes.Keys.Where(p => p.D == changed.dimension
            && Math.Abs(p.X - changed.X) <= 9 && Math.Abs(p.Z - changed.Z) <= 9).ToArray()) routes.Remove(key);
    }

    internal void Advance(ref int budget, Stopwatch watch)
    {
        while (budget > 6 && jobs.TryPeek(out var job) && watch.Elapsed.TotalMilliseconds < 2)
        {
            if (!routes.TryGetValue(job.Origin, out var live) || !ReferenceEquals(live, job)) { jobs.Dequeue(); continue; }
            if (!job.Pending.TryDequeue(out var step, out _)) { job.Complete = true; jobs.Dequeue(); continue; }
            var weights = step.Air ? job.Air : job.Water;
            if (step.Weight < weights.GetValueOrDefault(step.Cell)) continue;
            foreach (var facing in BlockFacing.ALLFACES)
            {
                var next = new Cell(step.Cell.X + facing.Normali.X, step.Cell.Y + facing.Normali.Y,
                    step.Cell.Z + facing.Normali.Z, step.Cell.D);
                if (Math.Abs(next.X - job.Origin.X) > 8 || Math.Abs(next.Z - job.Origin.Z) > 8
                    || next.Y < job.Origin.Y - 16 || next.Y > job.Origin.Y + (step.Air ? 8 : 0)
                    || !step.Air && next.Y > step.Cell.Y || weights.Count >= 1024) continue;
                budget--; Reads++;
                var block = read(next.Pos);
                if (block is null) continue;
                // Falling leakage may cross air vertically, but dry air is not a
                // lateral water conduit. Rain runoff requires wet/permeable ground.
                if (!step.Air && block.Id == 0 && next.Y == step.Cell.Y) continue;
                var permeability = step.Air ? AirPermeability(block) : WaterPermeability(block);
                if (permeability == 0 && (job.Barriers.Count < 256 || job.Barriers.ContainsKey(next)))
                    job.Barriers[next] = (byte)(job.Barriers.GetValueOrDefault(next) | (step.Air ? 2 : 1));
                var weight = step.Weight * permeability * (step.Air ? .85 : .8);
                if (weight < .002 || weight <= weights.GetValueOrDefault(next) + .00001) continue;
                weights[next] = weight;
                job.Pending.Enqueue((next, weight, step.Air), -weight);
            }
        }
    }

    internal static double WaterPermeability(Block block)
    {
        if (block.Id == 0 || block.IsLiquid() || !string.IsNullOrEmpty(block.LiquidCode)) return 1;
        // Solid construction is waterproof. Natural soils transmit slowly; clay and
        // intact bedrock require actual permeable cracks rather than radius seepage.
        var path = block.Code?.Path ?? "";
        if (path.StartsWith("clay", StringComparison.Ordinal) || path.StartsWith("rock-", StringComparison.Ordinal)) return 0;
        return block.BlockMaterial switch {
            EnumBlockMaterial.Sand => .65, EnumBlockMaterial.Gravel => .8,
            EnumBlockMaterial.Soil => .35, EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves => 1,
            _ => 0 };
    }
    private static double AirPermeability(Block block) => block.Id == 0 || block.IsLiquid()
        || block.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves ? 1 : 0;
}

