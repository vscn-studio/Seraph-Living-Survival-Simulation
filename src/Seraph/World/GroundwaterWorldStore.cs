using System.Text.Json;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.World;

/// <summary>Server-only sparse persistence for invisible aquifer regions.</summary>
internal sealed class GroundwaterWorldStore : IDisposable
{
    private const string SaveKey = "vsslssmod:groundwater-v1";
    private readonly ICoreServerAPI api;
    private readonly Dictionary<RegionKey, Entry> entries = new();
    private readonly long worldSeed;
    private bool dirty;

    private readonly record struct RegionKey(int X, int Z, int Dimension);
    private sealed class Entry
    {
        public GroundwaterState State { get; set; }
        public double LastGameSecond { get; set; }
    }
    private sealed class SaveEnvelope
    {
        [System.Text.Json.Serialization.JsonRequired]
        public string Format { get; set; } = "seraph.release.groundwater";
        [System.Text.Json.Serialization.JsonRequired]
        public int Schema { get; set; } = 1;
        [System.Text.Json.Serialization.JsonRequired]
        public List<SaveEntry> Entries { get; set; } = [];
    }
    private sealed class SaveEntry
    {
        public int X { get; set; }
        public int Z { get; set; }
        public int Dimension { get; set; }
        public GroundwaterState State { get; set; }
    }
    private readonly record struct TerrainMetrics(double SurfaceM, double DepressionM, double SlopeMPerM);

    internal GroundwaterWorldStore(ICoreServerAPI api)
    {
        this.api = api;
        worldSeed = api.World.Seed;
        Load();
        api.Event.ChunkColumnLoaded += Loaded;
        api.Event.ChunkColumnUnloaded += Unloaded;
    }

    private void Loaded(Vec2i coordinate, IWorldChunk[] chunks) => ResetColumn(coordinate.X, coordinate.Y);
    private void Unloaded(Vec3i coordinate) => ResetColumn(coordinate.X, coordinate.Z);
    private void ResetColumn(int x, int z)
    {
        var now = WorldCalendarTime.Seconds(api.World);
        foreach (var pair in entries)
            if ((pair.Key.X >> 1) == x && (pair.Key.Z >> 1) == z) pair.Value.LastGameSecond = now;
    }
    public void Dispose()
    {
        Save();
        api.Event.ChunkColumnLoaded -= Loaded;
        api.Event.ChunkColumnUnloaded -= Unloaded;
    }
    internal int PersistedRegionCount => entries.Count;

    internal GroundwaterState Query(BlockPos pos, double extractionL = 0)
    {
        var key = new RegionKey(pos.X >> 4, pos.Z >> 4, pos.dimension);
        var now = WorldCalendarTime.Seconds(api.World);
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { State = Derive(pos), LastGameSecond = now };
            entries[key] = entry;
        }
        var elapsed = Math.Min(7200, Math.Max(0, now - entry.LastGameSecond));
        if (api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null) { entry.LastGameSecond = now; return entry.State; }
        if (elapsed > 0 || extractionL > 0) Advance(key, entry, pos, now, extractionL);
        ExchangeLoadedNeighbors(key, entry, pos, now, elapsed / 30);
        dirty = true;
        return entry.State;
    }

    internal bool TryExtract(BlockPos pos, double amountL) => TryExtract(pos, amountL, out _);

    internal bool TryExtract(BlockPos pos, double amountL, out Action rollback)
    {
        rollback = () => { };
        if (!double.IsFinite(amountL) || amountL <= 0) return false;
        var before = Query(pos);
        if (before.StorageL < amountL) return false;
        // Recharge and lateral flow have already been applied by Query. Commit the
        // draw once, without running exchange a second time between balance checks.
        var key = new RegionKey(pos.X >> 4, pos.Z >> 4, pos.dimension);
        var ecologyRollback = WaterQuality.ExtractionRollback(api.World, pos);
        var restored = false;
        rollback = () => {
            if (restored) return;
            restored = true;
            entries[key].State = before; dirty = true; ecologyRollback();
        };
        entries[key].State = before with { StorageL = before.StorageL - amountL };
        WaterQuality.RecordExtraction(api.World, pos, amountL);
        dirty = true;
        return true;
    }

    internal void Save()
    {
        if (!dirty) return;
        var envelope = new SaveEnvelope { Entries = entries.Select(pair => new SaveEntry
        {
            X = pair.Key.X, Z = pair.Key.Z, Dimension = pair.Key.Dimension,
            State = pair.Value.State
        }).ToList() };
        api.WorldManager.SaveGame.StoreData(SaveKey, JsonSerializer.SerializeToUtf8Bytes(envelope));
        dirty = false;
    }

    private GroundwaterState Derive(BlockPos pos)
    {
        var accessor = api.World.BlockAccessor;
        var surface = accessor.GetTerrainMapheightAt(pos);
        var material = ClassifyAquifer(accessor, pos);
        var climate = accessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        var rainfall = LongTermRainfall(climate);
        var temperature = climate is { Temperature: var temp } && float.IsFinite(temp) ? temp : 15;
        var terrain = TerrainAt(pos, surface);
        var elevationAboveSea = Math.Clamp(surface - api.World.SeaLevel, -64, 160);
        var thickness = Math.Clamp(7 + rainfall * 14 + Math.Clamp((elevationAboveSea + 32) / 160d, 0, 1) * 5
            + terrain.DepressionM * 0.5 - terrain.SlopeMPerM * 20, 5, 32);
        return GroundwaterHydrologyModel.Derive(worldSeed, pos.X >> 4, pos.Z >> 4, surface, rainfall,
            temperature, material, thickness);
    }

    private void Advance(RegionKey key, Entry entry, BlockPos pos, double now, double extractionL)
    {
        var gameElapsed = Math.Clamp(now - entry.LastGameSecond, 0, 7200);
        var elapsed = gameElapsed / 30;
        if (elapsed <= 0 && extractionL <= 0) return;
        var accessor = api.World.BlockAccessor;
        var climate = accessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        var longTermRainfall = LongTermRainfall(climate);
        var currentRainfall = climate is { Rainfall: var rain } && float.IsFinite(rain) ? Math.Clamp(rain, 0, 1) : longTermRainfall;
        var temperature = climate is { Temperature: var temp } && float.IsFinite(temp) ? temp : entry.State.TemperatureC;
        var surface = accessor.GetTerrainMapheightAt(pos);
        var terrain = TerrainAt(pos, surface);
        var porosity = AquiferMaterials.Properties(entry.State.Material).DrainablePorosity;
        var elapsedDays = elapsed / 86400d;
        // WorldgenRainfall is the stable climate input; Rainfall is the current weather event.
        // These are gameplay calibration volumes, not a claim of real-world hydrologic units.
        var rainRecharge = (longTermRainfall * 80 + currentRainfall * 240) * (0.25 + porosity) * elapsedDays;
        var riverRecharge = (longTermRainfall * 30 + currentRainfall * 120)
            * Math.Clamp(terrain.DepressionM / 6d, 0, 1) * (0.25 + porosity) * elapsedDays;
        var ecologicalRecharge = WaterQuality.RegionAt(api.World, pos).RechargeMultiplier;
        rainRecharge *= ecologicalRecharge; riverRecharge *= ecologicalRecharge;
        var springFactor = Math.Clamp(terrain.SlopeMPerM * 5 + Math.Max(0, surface - api.World.SeaLevel) / 200d, 0, 1);
        var springDischarge = Math.Max(0, entry.State.StorageL / entry.State.CapacityL - 0.55) * springFactor * 70 * elapsedDays;
        var neighbourDepth = NeighbourDepth(key, entry.State.WaterTableDepthM);
        var step = GroundwaterHydrologyModel.Advance(entry.State, new GroundwaterInputs(
            elapsed, rainRecharge, riverRecharge, entry.State.StorageL * elapsed / 864000d,
            springDischarge, Math.Max(0, extractionL), temperature,
            Math.Clamp(0.25 + longTermRainfall * 0.65, 0, 1),
            neighbourDepth, 16));
        entry.State = step.State;
        entry.LastGameSecond += gameElapsed;
    }

    private void ExchangeLoadedNeighbors(RegionKey key, Entry entry, BlockPos pos, double now, double elapsed)
    {
        if (elapsed <= 0) return;
        // Only loaded/persisted cells participate. This keeps the world sparse and avoids
        // loading chunks solely for a background hydrology calculation.
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            var neighbourKey = new RegionKey(key.X + dx, key.Z + dz, key.Dimension);
            if (!entries.TryGetValue(neighbourKey, out var neighbour)) continue;
            var neighbourPos = pos.Copy().Set(pos.X + dx * 16, pos.Y, pos.Z + dz * 16);
            if (api.World.BlockAccessor.GetChunkAtBlockPos(neighbourPos) is null) continue;
            Advance(neighbourKey, neighbour, neighbourPos, now, 0);
            var transfer = GroundwaterHydrologyModel.Exchange(entry.State, neighbour.State,
                elapsed, 16);
            entry.State = transfer.A;
            neighbour.State = transfer.B;
            dirty = true;
        }
    }

    private double NeighbourDepth(RegionKey key, double fallback)
    {
        var best = fallback;
        var difference = 0d;
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            if (!entries.TryGetValue(new RegionKey(key.X + dx, key.Z + dz, key.Dimension), out var neighbour)) continue;
            var delta = Math.Abs(neighbour.State.WaterTableDepthM - fallback);
            if (delta > difference) { difference = delta; best = neighbour.State.WaterTableDepthM; }
        }
        return best;
    }

    private static AquiferMaterialClass ClassifyAquifer(IBlockAccessor accessor, BlockPos pos)
    {
        var samples = new[] { 2, 5, 8, 12 }
            .Select(offset => accessor.GetBlock(pos.Copy().Set(pos.X, Math.Max(1, pos.Y - offset), pos.Z)))
            .Select(block => Classify(block.Code?.Path ?? ""))
            .GroupBy(material => material)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => AquiferMaterials.Properties(group.Key).HydraulicConductivityMPerDay)
            .FirstOrDefault();
        return samples?.Key ?? AquiferMaterialClass.FracturedRock;
    }

    private TerrainMetrics TerrainAt(BlockPos pos, double surface)
    {
        var accessor = api.World.BlockAccessor;
        var north = accessor.GetTerrainMapheightAt(pos.Copy().Set(pos.X, pos.Y, pos.Z - 16));
        var south = accessor.GetTerrainMapheightAt(pos.Copy().Set(pos.X, pos.Y, pos.Z + 16));
        var west = accessor.GetTerrainMapheightAt(pos.Copy().Set(pos.X - 16, pos.Y, pos.Z));
        var east = accessor.GetTerrainMapheightAt(pos.Copy().Set(pos.X + 16, pos.Y, pos.Z));
        var average = (north + south + west + east) / 4d;
        var depression = Math.Max(0, average - surface);
        var slope = Math.Max(Math.Abs(east - west), Math.Abs(south - north)) / 32d;
        return new(surface, depression, Math.Clamp(slope, 0, 4));
    }

    private static double LongTermRainfall(ClimateCondition? climate)
    {
        if (climate is { WorldgenRainfall: var baseline } && float.IsFinite(baseline)) return Math.Clamp(baseline, 0, 1);
        if (climate is { Rainfall: var current } && float.IsFinite(current)) return Math.Clamp(current, 0, 1);
        return 0.5;
    }

    private static AquiferMaterialClass Classify(string path)
    {
        if (path.Contains("gravel", StringComparison.OrdinalIgnoreCase) || path.Contains("conglomerate", StringComparison.OrdinalIgnoreCase)) return AquiferMaterialClass.Gravel;
        if (path.Contains("sand", StringComparison.OrdinalIgnoreCase)) return path.Contains("fine", StringComparison.OrdinalIgnoreCase) ? AquiferMaterialClass.FineSand : AquiferMaterialClass.CoarseSand;
        if (path.Contains("clay", StringComparison.OrdinalIgnoreCase)) return AquiferMaterialClass.Clay;
        if (path.Contains("silt", StringComparison.OrdinalIgnoreCase) || path.Contains("soil", StringComparison.OrdinalIgnoreCase)) return AquiferMaterialClass.Silt;
        if (path.Contains("granite", StringComparison.OrdinalIgnoreCase) || path.Contains("rock", StringComparison.OrdinalIgnoreCase)) return AquiferMaterialClass.FracturedRock;
        return AquiferMaterialClass.FracturedRock;
    }

    private void Load()
    {
        try
        {
            var bytes = api.WorldManager.SaveGame.GetData(SaveKey);
            if (bytes is null) return;
            if (bytes.Length is 0 or > 4_000_000) throw new InvalidDataException("Invalid groundwater save length.");
            var envelope = JsonSerializer.Deserialize<SaveEnvelope>(bytes, new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            if (envelope?.Format != "seraph.release.groundwater" || envelope.Schema != 1 || envelope.Entries is null
                || envelope.Entries.Count > 8192 || envelope.Entries.Any(e => e is null || e.State.Validate() is not null)
                || envelope.Entries.Select(e => (e.X, e.Z, e.Dimension)).Distinct().Count() != envelope.Entries.Count)
                throw new InvalidDataException("Unsupported or invalid release groundwater format.");
            foreach (var item in envelope.Entries.Take(8192))
                if (item.State.Validate() is null)
                    entries[new(item.X, item.Z, item.Dimension)] = new() { State = item.State, LastGameSecond = WorldCalendarTime.Seconds(api.World) };
        }
        catch (Exception ex) { throw new InvalidDataException("Groundwater state rejected; original data preserved.", ex); }
    }
}
