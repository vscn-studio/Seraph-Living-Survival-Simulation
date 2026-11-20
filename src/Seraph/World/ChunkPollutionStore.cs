using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.World;

internal readonly record struct PollutionChunk(int X, int Z, int Dimension);
internal sealed record PollutionSnapshot(PollutionChunk Chunk, long At, PollutionLoad Load,
    int Manure, int Fires, int Carrion, Dictionary<string, int> Animals, double TemperatureC, int FreshWater, int SaltWater)
{
    public int SourceCount { get; init; } = -1;
    public RegionalSources Sources { get; init; } = new();
    public RegionalEnvironment Region { get; init; } = new();
}

/// <summary>Bounded main-thread capture of loaded chunks. Inspection never scans world blocks.</summary>
internal sealed partial class ChunkPollutionStore : IDisposable
{
    private sealed record LocalSource(int X, int Y, int Z, int RainY, PollutionLoad Load,
        IReadOnlyDictionary<string, double> Agents, bool Leaches = true, double GasEscape = 1, double SulfideFactor = 1,
        OrganicWasteKind Kind = OrganicWasteKind.Fecal, double WaterEscape = 1, long EntityId = 0);
    private sealed record Residue(LocalSource Point, RegionalEnvironment State)
    {
        public double? PondTransferHour { get; init; }
    }
    private sealed class Column
    {
        internal readonly Dictionary<int, IWorldChunk> Chunks = new();
        internal PollutionSnapshot? Snapshot;
        internal long Due;
        internal long Revision;
        internal bool EcologyDirty = true;
        internal bool FormatRejected;
        internal double EcologyHour;
        internal RegionalEnvironment? Region;
        internal double RegionalHour, WorldHour, ExtractedL;
        internal long SimulationDue;
        internal Dictionary<(int X, int Z), List<LocalSource>> Points = new();
        internal List<BlockPos> SealedSources = new();
        internal ushort[] Heights = [];
        internal Dictionary<(int X, int Y, int Z), Residue> Residues = new();
        internal Dictionary<(int X, int Z), (int X, int Y, int Z)[]> ResidueBins = new();
        internal BlockPos? WaterCell;
        internal readonly Dictionary<(int X, int Y, int Z), PondCell> PondCells = new();
    }
    private sealed class Scan(PollutionChunk key, Column column, double hour, SeraphServerConfig config)
    {
        internal readonly SeraphServerConfig Config = config;
        internal readonly PollutionChunk Key = key;
        internal readonly Column Column = column;
        internal readonly long Revision = column.Revision;
        internal readonly bool SurveyEcology = column.EcologyDirty || column.Snapshot is null || hour >= column.EcologyHour;
        internal readonly KeyValuePair<int, IWorldChunk>[] Chunks = column.Chunks.OrderBy(p => p.Key).ToArray();
        internal int ChunkIndex, BlockIndex, Manure, Fires, Carrion, Fresh, Salt;
        internal bool Prepared;
        internal bool EntitiesPrepared, EntitiesComplete;
        internal BlockEntity[] BlockEntities = [];
        internal Entity[] Entities = [];
        internal int BlockEntityIndex, EntityIndex;
        internal bool ReadSolids, ReadFluids, FoundFresh, FoundSalt, SampleOnly;
        internal PollutionLoad Load;
        internal BlockPos? WaterCell;
        internal AirChemistry Emissions;
        internal int Fields, Wildlife;
        internal readonly SurfaceEcologySurvey Ecology = new();
        internal ushort[]? Heights;
        internal double Nitrogen, Phosphorus, Metals;
        internal readonly Dictionary<string, int> Animals = new(StringComparer.Ordinal);
        internal readonly HashSet<long> CountedEntities = new();
        internal readonly HashSet<string> CorpseOwners = new(StringComparer.Ordinal);
        internal readonly HashSet<(int X, int Y, int Z, long Entity)> SourceIds = new();
        internal readonly Dictionary<(int X, int Z), List<LocalSource>> Points = new();
        internal readonly List<BlockPos> SealedSources = new();
    }
    private readonly ICoreServerAPI api;
    private readonly SlowCallbackProbe tickProbe = new("server.pollution.tick");
    private readonly SlowCallbackProbe reconcileProbe = new("server.pollution.reconcile");
    private readonly SlowCallbackProbe simulationProbe = new("server.pollution.simulate");
    private readonly SlowCallbackProbe saveProbe = new("server.pollution.save");
    private readonly Func<SeraphServerConfig> config;
    private readonly Dictionary<PollutionChunk, Column> columns = new();
    private readonly PriorityQueue<(PollutionChunk Key, Column Column), long> captureDue = new();
    private readonly PriorityQueue<(PollutionChunk Key, Column Column), long> simulationDue = new();
    private readonly record struct BlockSource(bool Manure, double Chemical, bool Fresh, bool Salt, bool Forest, bool Vegetation, double Flame, double Metals);
    private readonly Dictionary<int, BlockSource> sourceBlocks = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BlockEntity, FuelMemory> fuelMemory = new();
    private sealed class FuelMemory { internal BurningFuel Fuel; }
    private const string RegionKey = "vsslssmod:environment-v1";
    private readonly List<int> palette = new();
    private readonly Dictionary<int, (bool Animal, bool Biological)> animalTypes = new();
    private Scan? scan;
    private long reconcileAt;
    private bool disposed;
    private int classifiedCount;
    private SeraphServerConfig? classifiedConfig;
    private long lastError = long.MinValue / 2;
    private static readonly FieldInfo? FluidLayer = Type.GetType("Vintagestory.Common.ChunkData, VintagestoryLib")?.GetField("fluidsLayer");
    private readonly long listener;
    private readonly PollutionRouteCache routes;
    internal PollutionPlantCache Plants { get; }

    internal ChunkPollutionStore(ICoreServerAPI api, Func<SeraphServerConfig> config)
    {
        this.api = api; this.config = config;
        routes = new(ReadRouteBlock);
        Plants = new(ReadRouteBlock);
        api.Event.ChunkColumnLoaded += OnColumnLoaded;
        api.Event.ChunkColumnUnloaded += OnColumnUnloaded;
        api.Event.OnEntitySpawn += OnSourceEntity;
        api.Event.OnEntityDespawn += OnSourceDespawn;
        api.Event.OnEntityDeath += OnSourceDeath;
        listener = api.Event.RegisterGameTickListener(_ => Tick(), 50);
    }

    private void OnSourceEntity(Entity entity)
    {
        if (!disposed && (entity is EntityItem || !entity.Alive
            || entity.Code is { } code && OrganicPollutionSource.IsPlayerCorpse(code)))
            WaterQuality.Invalidate(api.World, entity.Pos.AsBlockPos);
    }
    private void OnSourceDespawn(Entity entity, EntityDespawnData _) => OnSourceEntity(entity);
    private void OnSourceDeath(Entity entity, DamageSource _)
    {
        if (!disposed) WaterQuality.Invalidate(api.World, entity.Pos.AsBlockPos);
    }

    private void OnColumnLoaded(Vec2i coordinate, IWorldChunk[] chunks)
    {
        if (disposed) return;
        var key = new PollutionChunk(coordinate.X, coordinate.Y, 0);
        var now = api.World.ElapsedMilliseconds;
        if (!columns.TryGetValue(key, out var column))
        {
            columns[key] = column = new();
            simulationDue.Enqueue((key, column), now);
        }
        column.Chunks.Clear();
        for (var y = 0; y < chunks.Length; y++)
            if (chunks[y] is { Disposed: false } chunk) column.Chunks[y] = chunk;
        column.Revision++; column.EcologyDirty = true;
        Schedule(key, column, now + 2000);
    }

    private void OnColumnUnloaded(Vec3i coordinate)
    {
        var key = new PollutionChunk(coordinate.X, coordinate.Z, 0);
        if (disposed || !columns.Remove(key, out var column)) return;
        Persist(column);
        if (scan is { } active && ReferenceEquals(active.Column, column)) scan = null;
    }

    internal void Tick()
    {
        if (disposed) return;
        using var timing = tickProbe.Measure(api);
        var now = api.World.ElapsedMilliseconds;
        try { Advance(now); }
        catch (Exception ex)
        {
            if (scan is { } failed) Schedule(failed.Key, failed.Column, now + 60000);
            scan = null;
            reconcileAt = Math.Max(reconcileAt, now + 5000);
            LogFailure(now, ex);
        }
    }

    private void LogFailure(long now, Exception ex)
    {
        if (now - lastError < 30000) return;
        lastError = now;
        api.Logger.Warning("[vsslssmod] Pollution capture deferred; retaining previous sample: {0}", ex);
    }

    private void Advance(long now)
    {
        var settings = config();
        if (classifiedCount != api.World.Blocks.Count || !ReferenceEquals(classifiedConfig, settings))
        {
            ClassifyBlocks(); classifiedCount = api.World.Blocks.Count; classifiedConfig = settings;
            foreach (var pair in columns)
            { pair.Value.Revision++; pair.Value.EcologyDirty = true; Schedule(pair.Key, pair.Value, now); }
        }
        // Loaded/unloaded events maintain normal columns. A slow reconciliation covers
        // startup and dimension-specific loaders without cloning the world's list every tick.
        if (now >= reconcileAt) { Reconcile(now); reconcileAt = now + 60000; }
        Simulate(now);
        var watch = Stopwatch.StartNew();
        var budget = 8192;
        // Reserve transport work before capture so a dense storage chunk cannot
        // indefinitely starve connectivity and plant jobs.
        var routeBudget = 256;
        Plants.Advance(now, 128, watch);
        routes.Advance(ref routeBudget, watch);
        AdvancePonds();
        var completed = 0;
        while (budget > 0 && completed < 8 && watch.Elapsed.TotalMilliseconds < 2)
        {
            if (scan is null)
            {
                while (captureDue.TryPeek(out var next, out var due) && due <= now)
                {
                    captureDue.Dequeue();
                    if (next.Column.Due != due || !columns.TryGetValue(next.Key, out var live) || !ReferenceEquals(live, next.Column)) continue;
                    next.Column.Due = long.MaxValue;
                    scan = new(next.Key, next.Column, WorldCalendarTime.Hours(api.World), settings);
                    break;
                }
                if (scan is null) break;
            }
            if (!Step(scan, ref budget)) continue;
            Finish(scan, now); scan = null; completed++;
        }
    }

    private void Schedule(PollutionChunk key, Column column, long at)
    {
        column.Due = at;
        captureDue.Enqueue((key, column), at);
    }

    private void Reconcile(long now)
    {
        using var timing = reconcileProbe.Measure(api);
        // Derive strides through the API, including the engine's dimension encoding.
        var strideZ = api.WorldManager.ChunkIndex3D(0, 0, 1);
        var strideY = api.WorldManager.ChunkIndex3D(0, 1, 0);
        if (strideZ <= 0 || strideY <= 0) return;
        var present = new HashSet<PollutionChunk>();
        var loaded = api.WorldManager.AllLoadedChunks;
        foreach (var pair in loaded)
        {
            if (pair.Value.Disposed) continue;
            var encodedY = (int)(pair.Key / strideY);
            var key = new PollutionChunk((int)(pair.Key % strideZ), (int)(pair.Key % strideY / strideZ), encodedY / 1024);
            present.Add(key);
            if (!columns.TryGetValue(key, out var column))
            {
                columns[key] = column = new();
                simulationDue.Enqueue((key, column), now);
                Schedule(key, column, now);
            }
            var y = encodedY % 1024;
            if (!column.Chunks.TryGetValue(y, out var old) || !ReferenceEquals(old, pair.Value))
            { column.Chunks[y] = pair.Value; column.Revision++; column.EcologyDirty = true;
                if (column.Due > now) Schedule(key, column, now); }
        }
        foreach (var key in columns.Keys.ToArray())
        {
            if (!present.Contains(key)) { Persist(columns[key]); columns.Remove(key); continue; }
            var column = columns[key];
            foreach (var y in column.Chunks.Where(p => p.Value.Disposed
                || !loaded.TryGetValue(api.WorldManager.ChunkIndex3D(key.X, p.Key + key.Dimension * 1024, key.Z), out var liveChunk)
                || !ReferenceEquals(liveChunk, p.Value)).Select(p => p.Key).ToArray())
            { column.Chunks.Remove(y); column.Revision++; column.EcologyDirty = true; }
        }
    }

    internal void Invalidate(BlockPos pos)
    {
        var key = Key(pos);
        routes.Invalidate(pos);
        Plants.Invalidate(pos);
        if (!columns.TryGetValue(key, out var column)) return;
        column.Revision++;
        column.EcologyDirty = true;
        var due = api.World.ElapsedMilliseconds;
        if (column.Due > due) Schedule(key, column, due);
    }

    internal PollutionSnapshot? Get(BlockPos pos)
    {
        if (!columns.TryGetValue(Key(pos), out var column) || column.Chunks.Values.All(c => c.Disposed)) return null;
        return column.Snapshot;
    }
    internal RegionalEnvironment RegionAt(BlockPos pos) => Get(pos)?.Region ?? new();
    internal Action ExtractionRollback(BlockPos pos)
    {
        if (!columns.TryGetValue(Key(pos), out var column)) return () => { };
        var previous = column.ExtractedL;
        return () => { column.ExtractedL = previous; Persist(column); };
    }
    internal void RecordExtraction(BlockPos pos, double litres)
    {
        if (double.IsFinite(litres) && litres > 0 && columns.TryGetValue(Key(pos), out var column))
        {
            column.ExtractedL = Math.Min(1e6, column.ExtractedL + litres);
            Persist(column);
        }
    }
    internal PollutionSnapshot? Report(BlockPos pos)
    {
        // A map cell describes this column, independently of cursor height or its neighbours.
        return Get(pos);
    }
    internal IEnumerable<PollutionSnapshot> Snapshots => columns.Values.Where(c => c.Chunks.Values.Any(chunk => !chunk.Disposed))
        .Select(c => c.Snapshot).OfType<PollutionSnapshot>();
    internal SeraphServerConfig Config => config();
    internal static PollutionChunk Key(BlockPos pos) => new(pos.X >> 5, pos.Z >> 5, pos.dimension);

    internal PollutionLoad LoadAt(BlockPos pos)
    {
        var key = Key(pos);
        var result = default(PollutionLoad);
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!columns.TryGetValue(new(key.X + dx, key.Z + dz, key.Dimension), out var column)
                    || column.Snapshot is not { } snapshot || column.Chunks.Values.All(c => c.Disposed)) continue;
                // Continuous weights avoid sharp quality changes at chunk boundaries.
                var distanceX = Math.Abs(pos.X + .5 - ((key.X + dx) * 32 + 16));
                var distanceZ = Math.Abs(pos.Z + .5 - ((key.Z + dz) * 32 + 16));
                var weight = Math.Max(0, 1 - distanceX / 48) * Math.Max(0, 1 - distanceZ / 48);
                result += new PollutionLoad(0, 0, 0, snapshot.Load.Smoke * weight, 0);
                // Four-block spatial bins keep inspections bounded even in dense livestock/storage areas.
                for (var bx = (pos.X - 8) >> 2; bx <= (pos.X + 8) >> 2; bx++)
                    for (var bz = (pos.Z - 8) >> 2; bz <= (pos.Z + 8) >> 2; bz++)
                    {
                        if (!column.Points.TryGetValue((bx, bz), out var points)) continue;
                        foreach (var point in points)
                        {
                            if (pos.Y > point.Y) continue; // Runoff/infiltration cannot travel uphill.
                            var rainY = columns.TryGetValue(key, out var target) && target.Heights.Length == 1024
                                ? target.Heights[(pos.X & 31) + 32 * (pos.Z & 31)] : pos.Y;
                            if (point.Y >= point.RainY - 1 && rainY > point.RainY + 1) continue;
                            var distance = Math.Sqrt(Math.Pow(pos.X - point.X, 2) + Math.Pow(pos.Z - point.Z, 2));
                            var local = point.Leaches ? routes.Weight(new(point.X, point.Y, point.Z, key.Dimension), pos) : 0;
                            result += point.Load.Scale(local * point.WaterEscape);
                        }
                    }
            }
        return result;
    }

    // Uses the bounded main-thread capture cache. Air/contact proximity has no uphill runoff rule.
    internal IReadOnlyDictionary<string, double> DiseaseSourcesAt(BlockPos pos)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var key = Key(pos);
        var inspected = 0;
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!columns.TryGetValue(new(key.X + dx, key.Z + dz, key.Dimension), out var column)
                    || column.Chunks.Values.All(c => c.Disposed)) continue;
                foreach (var point in NearbyPoints(column, pos))
                {
                    if (++inspected > 256) return result;
                    var distance = Math.Sqrt(Math.Pow(pos.X - point.X, 2) + Math.Pow(pos.Y - point.Y, 2) + Math.Pow(pos.Z - point.Z, 2));
                    if (distance >= 8 || point.GasEscape <= 0) continue;
                    void Add(string id, double value) { if (value > 0) result[id] = Math.Min(100, result.GetValueOrDefault(id) + value); }
                    // Use identified source/configuration loads; carrion is not automatically anthrax.
                    foreach (var agent in point.Agents)
                        Add(agent.Key, agent.Value * point.GasEscape * routes.Weight(new(point.X, point.Y, point.Z, key.Dimension), pos, air: true)
                            * Math.Max(0, 1 - distance / (Pathogens.Airborne(agent.Key) ? 8 : 2)));
                }
            }
        return result;
    }

    internal (double Generic, IReadOnlyDictionary<string, double> Agents) WaterResidueAt(BlockPos pos, bool ground)
    {
        // A completed pond has one water ledger. Do not add local source reservoirs
        // to the same pollution a second time when withdrawing from it.
        if (!ground && WaterQuality.Classifier(api.World)?.PondAt(pos) is not null)
        {
            var pond = PondAt(pos);
            return (pond.Generic, pond.Agents);
        }
        var generic = 0d;
        var agents = PathogenLoads.Empty;
        var immediateGeneric = 0d;
        var immediateAgents = PathogenLoads.Empty;
        var key = Key(pos);
        var inspected = 0;
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!columns.TryGetValue(new(key.X + dx, key.Z + dz, key.Dimension), out var column)
                    || column.Chunks.Values.All(c => c.Disposed)) continue;
                foreach (var site in NearbySites(column, pos))
                {
                    if (!column.Residues.TryGetValue(site, out var residue)) continue;
                    var point = residue.Point;
                    if (pos.Y > point.Y || Math.Abs(pos.X - point.X) > 8 || Math.Abs(pos.Z - point.Z) > 8) continue;
                    var rainY = columns.TryGetValue(key, out var target) && target.Heights.Length == 1024
                        ? target.Heights[(pos.X & 31) + 32 * (pos.Z & 31)] : pos.Y;
                    if (point.Y >= point.RainY - 1 && rainY > point.RainY + 1) continue;
                    var distance = Math.Sqrt(Math.Pow(pos.X - point.X, 2) + Math.Pow(pos.Z - point.Z, 2));
                    var weight = routes.Weight(new(point.X, point.Y, point.Z, key.Dimension), pos);
                    generic += (ground ? residue.State.GroundPathogens : residue.State.SurfacePathogens) * weight;
                    agents = PathogenLoads.Add(agents, PathogenLoads.Scale(ground ? residue.State.GroundAgents : residue.State.SurfaceAgents, weight));
                }

                // Face-adjacent exposed waste must affect water immediately. The
                // regional residue and route caches update on a slower schedule,
                // which otherwise lets the map count a new source while a nearby
                // water tooltip still reports clean water for several minutes.
                foreach (var point in NearbyPoints(column, pos))
                {
                    if (++inspected > 256) break;
                    if (!point.Leaches || point.WaterEscape <= 0 || pos.Y > point.Y
                        || Math.Abs(pos.X - point.X) + Math.Abs(pos.Y - point.Y) + Math.Abs(pos.Z - point.Z) != 1) continue;
                    var rainY = columns.TryGetValue(key, out var target) && target.Heights.Length == 1024
                        ? target.Heights[(pos.X & 31) + 32 * (pos.Z & 31)] : pos.Y;
                    if (point.Y >= point.RainY - 1 && rainY > point.RainY + 1) continue;
                    var sourceLoad = point.Load.Fecal + point.Load.Carrion * .75 + point.Load.Animals * .12;
                    immediateGeneric += sourceLoad * point.WaterEscape;
                    immediateAgents = PathogenLoads.Add(immediateAgents, PathogenLoads.Scale(point.Agents, point.WaterEscape));
                }

            }
        generic = Math.Max(generic, immediateGeneric);
        foreach (var (agent, value) in immediateAgents)
            if (value > agents.GetValueOrDefault(agent))
                agents = PathogenLoads.Add(agents, new Dictionary<string, double> { [agent] = value - agents.GetValueOrDefault(agent) });
        // Root interception reduces only arriving extra surface contamination,
        // never source baselines, salt or groundwater. Maximum retention is 30%.
        var riparian = ground ? 1 : PollutionTransport.RiparianRetention(Plants.At(pos, api.World.ElapsedMilliseconds, water: true).Active);
        // Previously mixed water remains local after a pond opens, grows beyond
        // the small-body budget, or awaits reclassification.
        var retainedWater = ground ? new PondContamination() : PondAt(pos);
        return (generic * riparian + retainedWater.Generic,
            PathogenLoads.Add(PathogenLoads.Scale(agents, riparian), retainedWater.Agents));
    }

    private static IEnumerable<(int X, int Y, int Z)> NearbySites(Column column, BlockPos pos)
    {
        for (var x = (pos.X - 8) >> 2; x <= (pos.X + 8) >> 2; x++)
            for (var z = (pos.Z - 8) >> 2; z <= (pos.Z + 8) >> 2; z++)
                if (column.ResidueBins.TryGetValue((x, z), out var sites))
                    foreach (var site in sites) yield return site;
    }
    private static IEnumerable<LocalSource> NearbyPoints(Column column, BlockPos pos)
    {
        for (var x = (pos.X - 8) >> 2; x <= (pos.X + 8) >> 2; x++)
            for (var z = (pos.Z - 8) >> 2; z <= (pos.Z + 8) >> 2; z++)
                if (column.Points.TryGetValue((x, z), out var sites))
                    foreach (var site in sites) yield return site;
    }
    private static void IndexResidues(Column column) => column.ResidueBins = column.Residues.Keys
        .GroupBy(p => (p.X >> 2, p.Z >> 2)).ToDictionary(g => g.Key, g => g.ToArray());

    private static void AddSource(Scan job, int x, int y, int z, PollutionLoad load, long entityId = 0, IReadOnlyDictionary<string, double>? agents = null, bool leaches = true, double gasEscape = 1, double sulfideFactor = 1,
        OrganicWasteKind kind = OrganicWasteKind.Fecal, double waterEscape = 1)
    {
        job.SourceIds.Add((x, y, z, entityId));
        job.Heights ??= job.Chunks.Select(p => p.Value).FirstOrDefault(c => !c.Disposed)?.MapChunk?.RainHeightMap?.ToArray() ?? [];
        var rainY = job.Heights.Length == 1024 ? job.Heights[(x & 31) + 32 * (z & 31)] : y;
        var bin = (x >> 2, z >> 2);
        if (!job.Points.TryGetValue(bin, out var points)) job.Points[bin] = points = new();
        agents ??= PathogenLoads.Add(PathogenLoads.Scale(job.Config.AnimalWastePathogens, load.Animals * .12),
            PathogenLoads.Scale(job.Config.CarrionPathogens, load.Carrion * .75));
        points.Add(new(x, y, z, rainY, load, agents, leaches, gasEscape, sulfideFactor, kind, waterEscape, entityId));
    }

    private void ClassifyBlocks()
    {
        sourceBlocks.Clear();
        var settings = config();
        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is not { } code) continue;
            var name = code.ToString();
            var manure = code.Domain is "vsslssmod" or "naturalfertilizer"
                && (code.Path.StartsWith("smallpoop-", StringComparison.Ordinal) || code.Path.StartsWith("compostdung-", StringComparison.Ordinal)
                    || code.Path.StartsWith("dung-", StringComparison.Ordinal) || code.Path.StartsWith("chickenpoop", StringComparison.Ordinal));
            const double chemical = 0; // Obsolete chemistry configuration is ignored.
            var salt = block.LiquidCode == "saltwater";
            var fresh = block.LiquidCode == "water";
            var plant = PollutionVegetation.Classify(block);
            var forest = plant.LeafUnits >= 1;
            var vegetation = plant.LeafUnits > 0 && !forest;
            var flame = code.Domain == "game" && (code.Path == "fire" || code.Path.StartsWith("fire-", StringComparison.Ordinal)) ? 1d
                : code.Domain == "game" && code.Path.StartsWith("torch-", StringComparison.Ordinal)
                    && !code.Path.Contains("extinct", StringComparison.Ordinal) && !code.Path.Contains("burnedout", StringComparison.Ordinal) ? .05 : 0;
            // Other mods can declare an actual waste source, without treating natural ore as pollution.
            const double metals = 0;
            if (manure || chemical > 0 || salt || fresh || forest || vegetation || flame > 0 || metals > 0)
                sourceBlocks[block.Id] = new(manure, chemical, fresh, salt, forest, vegetation, flame, metals);
        }
    }

    private bool Step(Scan job, ref int budget)
    {
        if (!columns.TryGetValue(job.Key, out var current) || !ReferenceEquals(current, job.Column)) return true;
        if (job.SurveyEcology && !job.Ecology.Complete)
        {
            CaptureEcology(job, ref budget);
            return false;
        }
        if (job.ChunkIndex >= job.Chunks.Length) return true;
        var pair = job.Chunks[job.ChunkIndex];
        var chunk = pair.Value;
        if (chunk.Disposed) { Next(job); return false; }
        if (!job.Prepared)
        {
            if (!CaptureEntities(job, chunk, ref budget)) return false;
            if (!chunk.Unpack_ReadOnly()) { Next(job); return false; }
            palette.Clear(); chunk.Data.FuzzyListBlockIds(palette);
            job.ReadSolids = palette.Any(id => sourceBlocks.TryGetValue(id, out var s)
                && (s.Manure || s.Chemical > 0 || s.Flame > 0 || s.Metals > 0));
            // Flora and water presence are estimates. Exact reads are reserved for actual pollutants.
            job.SampleOnly = !palette.Any(id => sourceBlocks.TryGetValue(id, out var s)
                && (s.Manure || s.Chemical > 0 || s.Flame > 0 || s.Metals > 0));
            // Native FuzzyListBlockIds only lists solids. A null native fluid layer is empty;
            // other implementations use bounded public GetFluid reads instead.
            job.ReadFluids = FluidLayer?.DeclaringType?.IsInstanceOfType(chunk.Data) != true || FluidLayer.GetValue(chunk.Data) is not null;
            if (!job.ReadSolids && !job.ReadFluids)
            { Next(job); return false; }
            job.Prepared = true;
        }
        if (!chunk.Unpack_ReadOnly()) { Next(job); return false; }
        // Release control after each small read batch, even for a water-filled column.
        var limit = job.SampleOnly ? 512 : 32768;
        var readsPerCell = (job.ReadSolids ? 1 : 0) + (job.ReadFluids ? 1 : 0);
        if (budget < readsPerCell) { budget = 0; return false; }
        var end = Math.Min(limit, job.BlockIndex + Math.Min(512, budget) / readsPerCell);
        for (; job.BlockIndex < end; job.BlockIndex++, budget -= readsPerCell)
        {
            // A 4-block lattice samples all three axes rather than repeatedly reading x=0.
            var index = job.SampleOnly ? (job.BlockIndex % 8 * 4 + 2)
                + 32 * ((job.BlockIndex / 8 % 8 * 4 + 2) + 32 * (job.BlockIndex / 64 * 4 + 2)) : job.BlockIndex;
            var id = job.ReadSolids ? chunk.Data.GetBlockId(index, BlockLayersAccess.Solid) : 0;
            if (job.ReadSolids && sourceBlocks.TryGetValue(id, out var source))
            {
                var local = new PollutionLoad(source.Manure ? 1 : 0, 0, 0, 0, source.Chemical + source.Metals * .01);
                if (source.Manure) job.Manure++;
                job.Load += local;
                if (local.Fecal > 0 || local.Chemical > 0)
                    AddSource(job, job.Key.X * 32 + index % 32, pair.Key * 32 + index / 1024,
                        job.Key.Z * 32 + index / 32 % 32, local);
                job.Metals += source.Metals;
                if (source.Flame > 0) { job.SourceIds.Add((job.Key.X * 32 + index % 32, pair.Key * 32 + index / 1024,
                        job.Key.Z * 32 + index / 32 % 32, 0));
                    job.Emissions += RegionalEnvironmentModel.Combustion(BurningFuel.Wood).Scale(source.Flame);
                    job.Load += new PollutionLoad(0, 0, 0, source.Flame, 0); }
            }
            if (job.ReadFluids && sourceBlocks.TryGetValue(chunk.Data.GetFluid(index), out var fluid))
            {
                if (fluid.Fresh && !job.FoundFresh) { job.Fresh++; job.FoundFresh = true; }
                if (fluid.Salt && !job.FoundSalt) { job.Salt++; job.FoundSalt = true; }
                if ((fluid.Fresh || fluid.Salt) && (job.WaterCell is null || job.WaterCell.Y < pair.Key * 32 + index / 1024))
                    job.WaterCell = new(job.Key.X * 32 + index % 32, pair.Key * 32 + index / 1024,
                        job.Key.Z * 32 + index / 32 % 32, job.Key.Dimension);
                if (!job.ReadSolids && job.FoundFresh && job.FoundSalt) { Next(job); return false; }
            }
        }
        if (job.BlockIndex >= limit) Next(job);
        return false;
    }

    private static void Next(Scan job)
    {
        job.ChunkIndex++; job.BlockIndex = 0; job.Prepared = false; job.FoundFresh = false; job.FoundSalt = false;
        job.EntitiesPrepared = job.EntitiesComplete = false; job.BlockEntities = []; job.Entities = [];
        job.BlockEntityIndex = job.EntityIndex = 0;
    }

    private void CaptureEcology(Scan job, ref int budget)
    {
        // Height-guided, resumable descent follows the approach researched in ecosystemflora.
        // Snapshot the map once; unpack each vertical chunk at most once in this read batch.
        job.Heights ??= job.Chunks.Select(p => p.Value).FirstOrDefault(c => !c.Disposed)?.MapChunk?.RainHeightMap?.ToArray() ?? [];
        var readable = new Dictionary<int, IWorldChunk?>();
        EcologyCell Read(int x, int y, int z)
        {
            var cy = y >> 5;
            if (!readable.TryGetValue(cy, out var chunk))
            {
                chunk = job.Column.Chunks.GetValueOrDefault(cy);
                if (chunk is null || chunk.Disposed || !chunk.Unpack_ReadOnly()) chunk = null;
                readable[cy] = chunk;
            }
            if (chunk is null) return EcologyCell.Unavailable;
            var id = chunk.Data.GetBlockId(x + 32 * (z + 32 * (y & 31)), BlockLayersAccess.Solid);
            if (id == 0) return EcologyCell.Open;
            sourceBlocks.TryGetValue(id, out var source);
            var flags = (source.Forest ? EcologyCell.Forest : 0) | (source.Vegetation ? EcologyCell.Vegetation : 0);
            return flags != 0 || api.World.Blocks[id].Replaceable >= 6000 ? flags : EcologyCell.Ground;
        }
        var top = job.Chunks.Length == 0 ? 0 : job.Chunks.Max(p => p.Key) * 32 + 31;
        budget -= job.Ecology.Advance(job.Heights, top, Math.Min(256, budget), Read);
    }

    private bool CaptureEntities(Scan job, IWorldChunk chunk, ref int budget)
    {
        if (job.EntitiesComplete) return true;
        if (!job.EntitiesPrepared)
        {
            job.BlockEntities = chunk.BlockEntities?.Values.ToArray() ?? [];
            var entities = chunk.Entities;
            job.Entities = entities is null ? [] : entities.Take(Math.Clamp(chunk.EntitiesCount, 0, entities.Length)).ToArray();
            foreach (var corpse in job.Entities.Where(e => e?.Code is not null && OrganicPollutionSource.IsPlayerCorpse(e.Code)))
                if (corpse.WatchedAttributes.GetString("ownerUID") is { Length: > 0 } uid) job.CorpseOwners.Add(uid);
            job.EntitiesPrepared = true;
        }
        // A dense farm/storage chunk must yield just like block reads do.
        var end = Math.Min(job.BlockEntities.Length, job.BlockEntityIndex + Math.Min(64, budget));
        for (; job.BlockEntityIndex < end; job.BlockEntityIndex++, budget--)
        {
            var be = job.BlockEntities[job.BlockEntityIndex];
            try
            {
            var burning = be is BlockEntityFirepit { IsBurning: true } or BlockEntityForge { IsBurning: true };
            if (burning)
            {
                var fuel = be is BlockEntityFirepit pit ? pit.fuelSlot.Itemstack : (be as BlockEntityForge)?.FuelSlot.Itemstack;
                var memory = fuelMemory.GetOrCreateValue(be);
                if (fuel?.Collectible.Code.Path is { } path) memory.Fuel = path.Contains("charcoal", StringComparison.Ordinal) ? BurningFuel.Charcoal
                    : path.Contains("coal", StringComparison.Ordinal) || path.Contains("coke", StringComparison.Ordinal) ? BurningFuel.Coal : BurningFuel.Wood;
                job.Fires++; job.SourceIds.Add((be.Pos.X, be.Pos.Y, be.Pos.Z, 0));
                job.Load += new PollutionLoad(0, 0, 0, 1, 0);
                job.Emissions += RegionalEnvironmentModel.Combustion(memory.Fuel);
            }
            if (be is BlockEntitySoilNutrition soil)
            {
                job.Fields++;
                var tree = new Vintagestory.API.Datastructures.TreeAttribute(); soil.ToTreeAttributes(tree);
                job.Nitrogen += Math.Max(0, soil.Nutrients[0] - 80) / 1000 + Math.Max(0, tree.GetFloat("slowN")) / 1500;
                job.Phosphorus += Math.Max(0, soil.Nutrients[1] - 80) / 1000 + Math.Max(0, tree.GetFloat("slowP")) / 1500;
                var excess = Math.Max(0, soil.Nutrients[0] - 80) / 1000 + Math.Max(0, soil.Nutrients[1] - 80) / 1000;
                _ = excess; // Native fertility remains independent of pollution.
            }
            if (OrganicPollutionSource.TryCompost(be, api.World, out var compost, out var aerated))
            {
                var decay = new PollutionLoad(0, 0, compost, 0, 0);
                job.Carrion++; job.Load += decay;
                AddSource(job, be.Pos.X, be.Pos.Y, be.Pos.Z, decay, sulfideFactor: aerated ? .2 : 1, kind: OrganicWasteKind.Compost);
            }
            if (be is BlockEntityContainer container)
            {
                var containment = OrganicPollutionSource.Containment(container);
                var attenuation = PollutionTransport.GasEscape(containment);
                if (attenuation <= 0)
                {
                    // Inspection retains sealed organic contents without injecting any
                    // source, water reservoir, gas mass or regional source count.
                    if (job.SealedSources.Count < 2048 && container.Inventory.Any(slot =>
                        LatrineWasteStorage.IsManure(slot.Itemstack)
                        || OrganicPollutionSource.DecayQuantity(api.World, slot.Itemstack) > 0))
                        job.SealedSources.Add(be.Pos.Copy());
                    continue;
                }
                var leaks = PollutionTransport.Leaks(containment);
                var quantity = container.Inventory.Where(slot => LatrineWasteStorage.IsManure(slot.Itemstack))
                    .Sum(slot => ManureMass.Total(slot.Itemstack!) / 100);
                var local = new PollutionLoad(Math.Min(4, quantity * .125), 0, 0, 0, 0);
                if (quantity > 0)
                {
                    job.Manure++; job.Load += local;
                    var agents = PathogenLoads.Empty;
                    foreach (var slot in container.Inventory.Where(slot => LatrineWasteStorage.IsManure(slot.Itemstack)))
                        agents = PathogenLoads.Add(agents, PathogenLoads.Scale(PathogenMetadata.Read(slot.Itemstack!),
                            ManureMass.Total(slot.Itemstack!) / 100 * .125));
                    AddSource(job, be.Pos.X, be.Pos.Y, be.Pos.Z, local, agents: agents, leaches: leaks, gasEscape: attenuation,
                        waterEscape: PollutionTransport.WaterEscape(containment));
                }
                foreach (var group in container.Inventory.Where(slot => !slot.Empty).GroupBy(slot => OrganicPollutionSource.Kind(slot.Itemstack)))
                {
                var rot = group.Sum(slot => OrganicPollutionSource.DecayQuantity(api.World, slot.Itemstack));
                if (rot > 0)
                {
                    var decay = new PollutionLoad(0, 0, Math.Min(4, rot * .125), 0, 0);
                    job.Carrion++; job.Load += decay;
                    AddSource(job, be.Pos.X, be.Pos.Y, be.Pos.Z, decay, leaches: leaks, gasEscape: attenuation,
                        kind: group.Key, waterEscape: PollutionTransport.WaterEscape(containment));
                }
                }
            }
            }
            catch (Exception ex) { LogFailure(api.World.ElapsedMilliseconds, ex); }
        }
        if (job.BlockEntityIndex < job.BlockEntities.Length) return false;
        end = Math.Min(job.Entities.Length, job.EntityIndex + Math.Min(64, budget));
        for (; job.EntityIndex < end; job.EntityIndex++, budget--)
        {
            var entity = job.Entities[job.EntityIndex];
            if (entity?.Code is null || !job.CountedEntities.Add(entity.EntityId) || OrganicPollutionSource.IsFantasy(entity.Code)) continue;
            var point = entity.Pos.AsBlockPos;
            if (entity is EntityItem item)
            {
                var manure = LatrineWasteStorage.IsManure(item.Itemstack);
                var rotQuantity = OrganicPollutionSource.DecayQuantity(api.World, item.Itemstack);
                var rot = rotQuantity > 0;
                if (!manure && !rot) continue;
                var quantity = Math.Min(4, (manure ? ManureMass.Total(item.Itemstack) / 100 : rotQuantity) * .125);
                var decay = new PollutionLoad(manure ? quantity : 0, 0, rot ? quantity : 0, 0, 0);
                if (manure) job.Manure++; else job.Carrion++;
                job.Load += decay; AddSource(job, point.X, point.Y, point.Z, decay, entity.EntityId,
                    manure ? PathogenLoads.Scale(PathogenMetadata.Read(item.Itemstack), quantity) : null,
                    kind: manure ? OrganicWasteKind.Fecal : OrganicPollutionSource.Kind(item.Itemstack));
                continue;
            }
            if (entity is EntityPlayer || OrganicPollutionSource.IsPlayerCorpse(entity.Code))
            {
                if (entity is EntityPlayer deadPlayer && !entity.Alive && job.CorpseOwners.Contains(deadPlayer.PlayerUID)) continue;
                if (!entity.Alive || OrganicPollutionSource.IsPlayerCorpse(entity.Code))
                {
                    job.Carrion++; job.Load += new PollutionLoad(0, 0, 1, 0, 0);
                    AddSource(job, point.X, point.Y, point.Z, new(0, 0, 1, 0, 0), entity.EntityId, kind: OrganicWasteKind.Corpse);
                }
                continue;
            }
            if (entity is not EntityAgent || entity.Properties is null) continue;
            if (!animalTypes.TryGetValue(entity.Properties.Id, out var organism))
            {
                var tags = api.EntityTagRegistry.SlowEnumerateTagNames(entity.Tags).ToHashSet();
                var excluded = tags.Contains("mechanical") || tags.Contains("monster");
                organism = (!excluded && tags.Contains("animal"), !excluded && (tags.Contains("animal") || tags.Contains("humanoid")));
                animalTypes[entity.Properties.Id] = organism;
            }
            if (!entity.Alive && organism.Biological) { job.Carrion++; job.Load += new PollutionLoad(0, 0, 1, 0, 0);
                AddSource(job, point.X, point.Y, point.Z, new(0, 0, 1, 0, 0), entity.EntityId, kind: OrganicWasteKind.Corpse); continue; }
            if (!entity.Alive || !organism.Animal) continue;
            var type = entity.Code.Domain + ":" + entity.Code.Path.Split('-')[0];
            job.Animals[type] = job.Animals.GetValueOrDefault(type) + 1;
            var weight = Math.Clamp(entity.Properties.Weight / 100d, .1, 4);
            job.Load += new PollutionLoad(0, weight, 0, 0, 0);
            // Livestock counts feed ecology. Gatherings shed a small waste load
            // using the configured animal profile; actual dung is a stronger source.
            AddSource(job, point.X, point.Y, point.Z, new(0, weight * .15, 0, 0, 0), entity.EntityId);

            if (entity.WatchedAttributes.GetInt("generation") <= 0) job.Wildlife++;
        }
        return job.EntitiesComplete = job.EntityIndex >= job.Entities.Length;
    }

    private void Finish(Scan job, long now)
    {
        if (!columns.TryGetValue(job.Key, out var column) || !ReferenceEquals(column, job.Column)
            || column.Chunks.Values.All(c => c.Disposed)) return;
        var pos = new BlockPos(job.Key.X * 32 + 16, 0, job.Key.Z * 32 + 16, job.Key.Dimension);
        pos.Y = api.World.BlockAccessor.GetTerrainMapheightAt(pos);
        var agents = PathogenLoads.Empty;
        foreach (var point in job.Points.Values.SelectMany(p => p)) agents = PathogenLoads.Add(agents, point.Agents);
        var source = new RegionalSources { Pathogens = agents, Emissions = job.Emissions, Fecal = job.Load.Fecal + job.Load.Animals * .12 + job.Load.Carrion * .75,
            Nitrogen = job.Nitrogen, Phosphorus = job.Phosphorus, Metals = job.Metals * .01,
            Forest = job.Ecology.ObservedColumns == 64 ? job.Ecology.ForestColumns / 64d
                : column.Snapshot?.Sources.Forest ?? 0,
            Vegetation = job.Ecology.ObservedColumns == 64 ? job.Ecology.VegetationColumns / 64d
                : column.Snapshot?.Sources.Vegetation ?? 0,
            Cultivation = Math.Clamp(job.Fields / 512d, 0, 1), Grazing = job.Load.Animals, Wildlife = job.Wildlife };
        if (column.Region is null)
        {
            column.Region = Restore(column) ?? RegionalEnvironmentModel.Create(source);
            column.RegionalHour = column.WorldHour = WorldCalendarTime.Hours(api.World);
        }
        column.Snapshot = new(job.Key, now, job.Load, job.Manure, job.Fires, job.Carrion, job.Animals,
            WaterQuality.Ambient(api.World, pos), job.Fresh, job.Salt)
            { Sources = source, Region = column.Region, SourceCount = job.SourceIds.Count };
        column.Points = job.Points;
        column.SealedSources = job.SealedSources;
        column.WaterCell = job.WaterCell;
        foreach (var point in job.Points.Values.SelectMany(p => p))
        {
            routes.Request(new(point.X, point.Y, point.Z, job.Key.Dimension), now);
            if (!point.Leaches) continue;
            var site = (point.X, point.Y, point.Z);
            if (!column.Residues.ContainsKey(site) && column.Residues.Count < 2048)
                column.Residues[site] = new(point, new());
        }
        foreach (var site in column.Residues.Keys) routes.Request(new(site.X, site.Y, site.Z, job.Key.Dimension), now);
        IndexResidues(column);
        // Clean columns still need terrain barriers when neighbouring sources are queried.
        // A local-only rescan can skip both ecology and AddSource.
        column.Heights = job.Heights ?? job.Chunks.Select(p => p.Value).FirstOrDefault(c => !c.Disposed)
            ?.MapChunk?.RainHeightMap?.ToArray() ?? column.Heights;
        if (job.SurveyEcology)
        {
            column.EcologyHour = WorldCalendarTime.Hours(api.World) + 1;
            column.EcologyDirty = column.Revision != job.Revision || job.Ecology.ObservedColumns != 64;
        }
        Schedule(job.Key, column, now + (column.Revision == job.Revision ? 60000 : 2000));
    }

    private void Simulate(long now)
    {
        using var timing = simulationProbe.Measure(api);
        var hour = WorldCalendarTime.Hours(api.World);
        if (!double.IsFinite(hour)) return;
        var updates = new List<(Column Column, RegionalEnvironment Region)>();
        for (var processed = 0; processed < 8 && simulationDue.TryPeek(out var nextDue, out var due) && due <= now; processed++)
        {
            simulationDue.Dequeue();
            if (!columns.TryGetValue(nextDue.Key, out var live) || !ReferenceEquals(live, nextDue.Column)) continue;
            var column = nextDue.Column;
            column.SimulationDue = now + 5000;
            simulationDue.Enqueue(nextDue, column.SimulationDue);
            if (column.Chunks.Values.All(c => c.Disposed) || column.Region is not { } old || column.Snapshot is not { } sample) continue;
            if (hour < column.RegionalHour || hour < column.WorldHour) column.RegionalHour = column.WorldHour = hour;
            if (hour - column.RegionalHour < RegionalEnvironmentModel.RegionalIntervalHours) continue;
            var key = nextDue.Key;
            var pos = new BlockPos(key.X * 32 + 16, 0, key.Z * 32 + 16, key.Dimension);
            pos.Y = api.World.BlockAccessor.GetTerrainMapheightAt(pos) + 1;
            var climate = api.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            var wind = api.World.BlockAccessor.GetWindSpeedAt(pos);
            var speed = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
            var incoming = default(AirChemistry);
            var magnitude = Math.Abs(wind.X) + Math.Abs(wind.Z);
            if (magnitude > .001)
            {
                if (columns.TryGetValue(new(key.X - Math.Sign(wind.X), key.Z, key.Dimension), out var x)
                    && Math.Abs(wind.X) > 0 && x.Region is { } xr && x.Chunks.Values.Any(c => !c.Disposed))
                    incoming += xr.Air.Scale(Math.Abs(wind.X) / magnitude);
                if (columns.TryGetValue(new(key.X, key.Z - Math.Sign(wind.Z), key.Dimension), out var z)
                    && Math.Abs(wind.Z) > 0 && z.Region is { } zr && z.Chunks.Values.Any(c => !c.Disposed))
                    incoming += zr.Air.Scale(Math.Abs(wind.Z) / magnitude);
            }
            var elapsed = Math.Min(.25, hour - column.RegionalHour);
            var next = RegionalEnvironmentModel.Regional(old, sample.Sources, elapsed,
                climate?.Rainfall ?? 0, speed, incoming, climate?.Temperature ?? 15);
            var active = column.Points.Values.SelectMany(p => p).GroupBy(p => (p.X, p.Y, p.Z)).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var pair in column.Residues.ToArray())
            {
                var current = (active.GetValueOrDefault(pair.Key) ?? []).Where(p => p.Leaches).ToArray();
                var localAgents = PathogenLoads.Empty;
                foreach (var point in current) localAgents = PathogenLoads.Add(localAgents, PathogenLoads.Scale(point.Agents, point.WaterEscape));
                var localBio = current.Sum(p => (p.Load.Fecal + p.Load.Animals * .12 + p.Load.Carrion * .75) * p.WaterEscape);
                var state = RegionalEnvironmentModel.Regional(pair.Value.State,
                    new RegionalSources { Fecal = localBio, Pathogens = localAgents }, elapsed,
                    climate?.Rainfall ?? 0, 0, temperatureC: climate?.Temperature ?? 15);
                var aquatic = Plants.At(new(pair.Key.X, pair.Key.Y, pair.Key.Z, key.Dimension), now, water: true).Aquatic;
                var retention = PollutionTransport.AquaticRetention(aquatic, elapsed);
                // Only persistent extra pollution decays here. Natural source
                // baselines are added at withdrawal and never purified by this sink.
                state = state with { SurfacePathogens = state.SurfacePathogens * retention,
                    SurfaceAgents = PathogenLoads.Scale(state.SurfaceAgents, retention) };
                column.Residues[pair.Key] = pair.Value with { State = state };
                if (current.Length == 0 && state.SoilPathogens + state.SurfacePathogens + state.GroundPathogens
                    + state.SoilAgents.Values.Sum() + state.SurfaceAgents.Values.Sum() + state.GroundAgents.Values.Sum() < 1e-6)
                    column.Residues.Remove(pair.Key);
            }
            column.RegionalHour += elapsed;
            if (hour - column.WorldHour >= RegionalEnvironmentModel.WorldIntervalHours)
            {
                next = RegionalEnvironmentModel.World(next, sample.Sources, hour - column.WorldHour, column.ExtractedL);
                column.ExtractedL = 0; column.WorldHour += Math.Min(1, hour - column.WorldHour);
            }
            updates.Add((column, next));
        }
        foreach (var (column, region) in updates)
        { column.Region = region; column.Snapshot = column.Snapshot! with { Region = region }; Persist(column); }
    }

    internal IEnumerable<(BlockPos Position, AirChemistry Gas, long EntityId)> OrganicGasSources()
    {
        foreach (var pair in columns)
        {
            if (pair.Value.Chunks.Values.All(c => c.Disposed)) continue;
            foreach (var p in pair.Value.Points.Values.SelectMany(p => p))
            {
                var bio = p.Load.Fecal + p.Load.Carrion;
                if (bio > 0 && p.GasEscape > 0)
                {
                    var gas = PollutionTransport.OrganicGas(p.Kind, bio * p.GasEscape);
                    yield return (new(p.X, p.Y, p.Z, pair.Key.Dimension), gas with { H2S = gas.H2S * p.SulfideFactor }, p.EntityId);
                }
            }
        }
    }

    internal PollutionDebugVolume[] DebugVolumes(BlockPos eye)
    {
        // Cache-only projection. Never request a chunk, route or synchronous world read.
        bool Visible(BlockPos p) => p.dimension == eye.dimension && Math.Abs(p.X - eye.X) <= 16
            && Math.Abs(p.Y - eye.Y) <= 16 && Math.Abs(p.Z - eye.Z) <= 16
            && columns.TryGetValue(Key(p), out var c) && c.Chunks.TryGetValue(p.Y >> 5, out var chunk) && !chunk.Disposed;
        int Distance(BlockPos p) => Math.Abs(p.X - eye.X) + Math.Abs(p.Y - eye.Y) + Math.Abs(p.Z - eye.Z);
        var sources = new List<(BlockPos Pos, PollutionDebugKind Kind, bool Water, bool Air)>();
        for (var x = (eye.X - 16) >> 5; x <= (eye.X + 16) >> 5; x++)
        for (var z = (eye.Z - 16) >> 5; z <= (eye.Z + 16) >> 5; z++)
        {
            if (!columns.TryGetValue(new(x, z, eye.dimension), out var column)) continue;
            foreach (var p in column.Points.Values.SelectMany(p => p))
            {
                var pos = new BlockPos(p.X, p.Y, p.Z, eye.dimension);
                if (p.Load.Fecal + p.Load.Carrion <= 0 || !Visible(pos)) continue;
                sources.Add((pos, p.GasEscape < 1 || !p.Leaches ? PollutionDebugKind.RestrictedSource
                    : PollutionDebugKind.Source, p.Leaches && p.WaterEscape > 0, p.GasEscape > 0));
            }
            sources.AddRange(column.SealedSources.Where(Visible).Select(p => (p, PollutionDebugKind.SealedSource, false, false)));
        }
        var nearest = sources.OrderBy(p => Distance(p.Pos)).DistinctBy(p => (p.Pos.X, p.Pos.Y, p.Pos.Z, p.Kind)).Take(32).ToArray();
        var volumes = new List<PollutionDebugVolume>();
        void Add(BlockPos p, PollutionDebugKind kind, double strength) => volumes.Add(new() {
            X = p.X, Y = p.Y, Z = p.Z, Dimension = p.dimension, Kind = kind, Strength = (float)strength });
        foreach (var p in nearest) Add(p.Pos, p.Kind, 1);
        foreach (var p in nearest.Where(p => p.Water || p.Air).Take(8))
            foreach (var cell in routes.DebugCells(p.Pos, p.Water, p.Air))
                if (Visible(cell.Position)) Add(cell.Position, cell.Kind, cell.Strength);
        return volumes.DistinctBy(p => (p.X, p.Y, p.Z, p.Kind)).Take(768).ToArray();
    }

    internal WaterBatch? MapWater(PollutionChunk key) => columns.TryGetValue(key, out var column) && column.WaterCell is { } pos
        ? WaterQuality.WorldSource(api.World, pos, config()) : null;

    private Block? ReadRouteBlock(BlockPos pos)
    {
        if (!columns.TryGetValue(Key(pos), out var column) || !column.Chunks.TryGetValue(pos.Y >> 5, out var chunk)
            || chunk.Disposed || !chunk.Unpack_ReadOnly()) return null;
        var index = (pos.X & 31) + 32 * ((pos.Z & 31) + 32 * (pos.Y & 31));
        var solid = chunk.Data.GetBlockId(index, BlockLayersAccess.Solid);
        // Fluid layers are exposed through GetFluid on generated chunks.  Reading
        // the fluid layer as a block id returns zero on older API implementations,
        // which would make a water channel look like dry air to the route cache.
        var fluid = chunk.Data.GetFluid(index);
        return api.World.Blocks[solid == 0 && fluid != 0 ? fluid : solid];
    }

    private RegionalEnvironment? Restore(Column column)
    {
        try
        {
            var bytes = column.Chunks.OrderBy(p => p.Key).Select(p => p.Value.GetModdata(RegionKey)).FirstOrDefault(b => b is not null);
            var debt = column.Chunks.OrderBy(p => p.Key).Select(p => p.Value.GetModdata(RegionKey + "-extracted"))
                .FirstOrDefault(b => b is not null);
            var sites = column.Chunks.OrderBy(p => p.Key).Select(p => p.Value.GetModdata(RegionKey + "-sites"))
                .FirstOrDefault(b => b is not null);
            if (bytes is null)
            {
                if (debt is not null || sites is not null || column.Chunks.Values.Any(c => c.GetModdata(PondKey) is not null))
                    throw new InvalidDataException("Regional root identity missing.");
                return null;
            }
            if (bytes.Length is 0 or >= 16384) throw new InvalidDataException("Invalid regional save length.");
            var saved = JsonSerializer.Deserialize<RegionalEnvironment>(bytes, new JsonSerializerOptions {
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            if (debt is not null)
            {
                if (debt.Length != 8 || !double.IsFinite(BitConverter.ToDouble(debt))
                    || BitConverter.ToDouble(debt) is < 0 or > 1e6)
                    throw new InvalidDataException("Invalid regional extraction data.");
                column.ExtractedL += BitConverter.ToDouble(debt);
            }
            if (sites is not null)
            {
                if (sites.Length is 0 or >= 2000000) throw new InvalidDataException("Invalid regional residue length.");
                var residues = JsonSerializer.Deserialize<Residue[]>(sites, new JsonSerializerOptions {
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
                if (residues is null || residues.Length > 2048
                    || residues.Any(site => site is null || site.State is null || !site.State.Valid() || !PathogenLoads.Valid(site.Point.Agents)
                        || site.PondTransferHour is { } transferred && (!double.IsFinite(transferred) || transferred < 0))
                    || residues.Select(site => (site.Point.X, site.Point.Y, site.Point.Z)).Distinct().Count() != residues.Length)
                    throw new InvalidDataException("Invalid regional residue data.");
                foreach (var site in residues) column.Residues[(site.Point.X, site.Point.Y, site.Point.Z)] = site with { PondTransferHour = PondHour };
            }
            RestorePonds(column);
            return saved?.Valid() == true ? saved : throw new InvalidDataException("Unsupported release regional format.");
        }
        catch (Exception ex) { column.FormatRejected = true; LogFailure(api.World.ElapsedMilliseconds, ex); throw new InvalidDataException("Regional state rejected; original data preserved.", ex); }
    }
    private static void Persist(Column column)
    {
        if (column.FormatRejected || column.Region is not { } region) return;
        var chunk = column.Chunks.OrderBy(p => p.Key).Select(p => p.Value).FirstOrDefault(c => !c.Disposed);
        if (chunk is null) return;
        chunk.SetModdata(RegionKey, JsonSerializer.SerializeToUtf8Bytes(region));
        chunk.SetModdata(RegionKey + "-sites", JsonSerializer.SerializeToUtf8Bytes(column.Residues.Values));
        chunk.SetModdata(RegionKey + "-extracted", BitConverter.GetBytes(column.ExtractedL));
        chunk.SetModdata(PondKey, JsonSerializer.SerializeToUtf8Bytes(column.PondCells.Values));
        chunk.MarkModified();
    }
    internal void Save() { using var timing = saveProbe.Measure(api); foreach (var column in columns.Values) Persist(column); }
    public void Dispose() { Save(); disposed = true; api.Event.UnregisterGameTickListener(listener);
        api.Event.ChunkColumnLoaded -= OnColumnLoaded; api.Event.ChunkColumnUnloaded -= OnColumnUnloaded;
        api.Event.OnEntitySpawn -= OnSourceEntity; api.Event.OnEntityDespawn -= OnSourceDespawn;
        api.Event.OnEntityDeath -= OnSourceDeath;
        columns.Clear(); captureDue.Clear(); simulationDue.Clear(); scan = null;
        pondWork?.Dispose(); pondWork = null; }
}
