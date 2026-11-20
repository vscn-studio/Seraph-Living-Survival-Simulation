using System.Text.Json;
using Seraph.Application;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

internal static class WaterQuality
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IWorldAccessor, SurfaceWaterClassifier> Classifiers = new();
    internal static void BindClassifier(IWorldAccessor world, SurfaceWaterClassifier classifier)
    { Classifiers.Remove(world); Classifiers.Add(world, classifier); }
    internal static SurfaceWaterClassifier? Classifier(IWorldAccessor world)
        => Classifiers.TryGetValue(world, out var classifier) ? classifier : null;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IWorldAccessor, GroundwaterWorldStore> GroundwaterStores = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IWorldAccessor, ChunkPollutionStore> PollutionStores = new();
    internal static void BindPollution(IWorldAccessor world, ChunkPollutionStore store)
    {
        PollutionStores.Remove(world);
        PollutionStores.Add(world, store);
    }
    internal static PollutionLoad PollutionAt(IWorldAccessor world, BlockPos pos) =>
        PollutionStores.TryGetValue(world, out var store) ? store.LoadAt(pos) : default;
    internal static IReadOnlyDictionary<string, double> DiseaseSourcesAt(IWorldAccessor world, BlockPos pos) =>
        PollutionStores.TryGetValue(world, out var store) ? store.DiseaseSourcesAt(pos) : PathogenLoads.Empty;
    internal static IReadOnlyDictionary<string, double> ConfiguredRespiratorySourcesAt(IWorldAccessor world,
        BlockPos pos, SeraphServerConfig config)
    {
        if (config.RespiratorySourcesByBlockCode.Count == 0) return PathogenLoads.Empty;
        var load = 0d;
        foreach (var sample in new[] { pos, pos.AddCopy(0, -1, 0) })
        {
            var code = world.BlockAccessor.GetBlock(sample).Code?.ToString();
            if (code is not null) load = Math.Max(load, config.RespiratorySourcesByBlockCode.GetValueOrDefault(code));
        }
        return load > 0 ? new Dictionary<string, double> { ["respiratory"] = load } : PathogenLoads.Empty;
    }
    internal static RegionalEnvironment RegionAt(IWorldAccessor world, BlockPos pos) =>
        PollutionStores.TryGetValue(world, out var store) ? store.RegionAt(pos) : new();
    internal static Action ExtractionRollback(IWorldAccessor world, BlockPos pos)
        => PollutionStores.TryGetValue(world, out var store) ? store.ExtractionRollback(pos) : () => { };
    internal static void RecordExtraction(IWorldAccessor world, BlockPos pos, double litres)
    { if (PollutionStores.TryGetValue(world, out var store)) store.RecordExtraction(pos, litres); }
    internal static WaterBatch ApplyPollution(IWorldAccessor world, BlockPos pos, WaterBatch batch,
        SeraphServerConfig config, bool groundwater, bool salt = false)
    {
        var load = PollutionAt(world, pos);
        var levels = PollutionModel.Levels(load);
        var residue = PollutionStores.TryGetValue(world, out var store)
            ? store.WaterResidueAt(pos, groundwater) : (Generic: 0d, Agents: PathogenLoads.Empty);
        var residual = residue.Generic;
        var turbidity = Math.Clamp(batch.Turbidity + (1 - Math.Exp(-residual)) * .6, 0, 1);
        return batch with
        {
            Source = load.Fecal > .01 || load.Carrion > .01 || residual > .01 ? "fecal"
                : batch.Source,
            // Spatial sources feed the persisted reservoirs. Do not bill that same
            // source again as an instantaneous generic exposure (or invent bacteria).
            EntericHazardPerL = Math.Clamp(batch.EntericHazardPerL + residual * config.FecalWaterHazardPerL
                + SurfaceWater.TurbidityHazard(Math.Max(0, turbidity - batch.Turbidity)), 0, 100),
            // Release water does not create a
            // separate generic chemical dose. Inorganic pollution affects turbidity.
            PathogensPerL = PathogenLoads.Add(batch.PathogensPerL, residue.Agents),
            ChemicalHazardPerL = 0,
            Turbidity = turbidity
        };
    }
    private sealed class WorldCache
    {
        internal readonly Dictionary<(int X, int Y, int Z, int D), (long At, double CalendarAt, int Fluid, string Kind, WaterBatch Batch)> Entries = new();
        internal SeraphServerConfig? Config;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IWorldAccessor, WorldCache> Caches = new();
    internal static void BindGroundwater(IWorldAccessor world, GroundwaterWorldStore store)
    {
        GroundwaterStores.Remove(world);
        GroundwaterStores.Add(world, store);
    }
    // Exact cells: no neighbouring clean/polluted cell is silently substituted. Bounded per-world cache.
    internal static void Invalidate(IWorldAccessor world, BlockPos changed)
    {
        if (PollutionStores.TryGetValue(world, out var store)) store.Invalidate(changed);
        if (Classifiers.TryGetValue(world, out var classifier)) classifier.InvalidateAt(changed);
        if (!Caches.TryGetValue(world, out var cache)) return;
        foreach (var key in cache.Entries.Keys.Where(k => k.D == changed.dimension
            && Math.Abs(k.X - changed.X) <= 8 && k.Y <= changed.Y && Math.Abs(k.Z - changed.Z) <= 8).ToArray())
            cache.Entries.Remove(key);
    }
    public static bool IsWater(ItemStack? liquid) => liquid?.Collectible.Code.Domain == "game"
        && liquid.Collectible.Code.Path is "waterportion" or "saltwaterportion" or "boilingwaterportion";

    public static WaterBatch? ReadLiquid(ItemStack? liquid, double ambient, SeraphServerConfig config)
    {
        if (!IsWater(liquid)) return null;
        var units = BlockLiquidContainerBase.GetContainableProps(liquid!)?.ItemsPerLitre ?? 0;
        if (!float.IsFinite(units) || units <= 0 || liquid!.StackSize <= 0) return null;
        var volume = liquid.StackSize / (double)units;
        if (WaterMetadata.HasTag(liquid)) return WaterMetadata.Read(liquid, volume);
        var boiling = liquid.Collectible.Code.Path == "boilingwaterportion";
        return new() { VolumeL = volume, TemperatureC = boiling ? 100 : Math.Clamp(ambient, -80, 110),
            Source = liquid.Collectible.Code.Path == "saltwaterportion" ? "salt" : "unverified",
            SaltGramsPerL = liquid.Collectible.Code.Path == "saltwaterportion" ? 35 : 0,
            EntericHazardPerL = config.FreshWaterHazardPerL };
    }

    public static double Ambient(IWorldAccessor world, BlockPos pos)
    {
        var temp = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues)?.Temperature;
        return temp is { } t && float.IsFinite(t) ? Math.Clamp(t, -80, 80) : 15;
    }

    // Read native cooling on a clone: information queries must not advance persistent item state.
    public static WaterBatch? ReadLiquidAt(IWorldAccessor world, ItemStack? liquid, double ambient, SeraphServerConfig config)
    {
        var batch = ReadLiquid(liquid, ambient, config);
        if (batch is null) return batch;
        // WaterMetadata stores the authoritative temperature. The game's default item
        // cooling moves every item toward 0 C (about 120 C per game hour), which makes
        // ordinary containers appear to freeze in seconds. Tagged water instead cools
        // gradually toward its local air temperature.
        if (WaterMetadata.HasTag(liquid!))
        {
            var updatedAt = liquid!.Attributes.GetDouble(WaterMetadata.TemperatureUpdatedAtKey, double.NaN);
            if (double.IsFinite(updatedAt) && WaterMetadata.TryCurrentTime(world, out var now) && now > updatedAt)
                return batch with { TemperatureC = StoredWaterCooling.Advance(batch.TemperatureC, ambient, now - updatedAt) };
            return batch;
        }
        if (!liquid!.Attributes.HasAttribute("temperature")) return batch;
        var temperature = liquid.Collectible.GetTemperature(world, liquid.Clone());
        return float.IsFinite(temperature) ? batch with { TemperatureC = Math.Clamp(temperature, -80, 110) } : null;
    }

    public static WaterBatch? WorldSource(IWorldAccessor world, BlockPos pos, SeraphServerConfig config)
    {
        var accessor = world.BlockAccessor;
        if (!WaterInteractionTarget.TryGetWaterAt(accessor, pos, out var fluid, out _)) return null;
        var cache = Caches.GetValue(world, static _ => new WorldCache());
        Classifiers.TryGetValue(world, out var classifier);
        var salt = fluid.Code?.Path?.Contains("saltwater", StringComparison.OrdinalIgnoreCase) == true
            || fluid.LiquidCode == "saltwater";
        var kind = salt ? "salt" : classifier?.Query(pos) ?? (pos.Y < world.SeaLevel - 4 ? "groundwater" : "surface");
        if (!ReferenceEquals(cache.Config, config)) { cache.Entries.Clear(); cache.Config = config; }
        var key = (pos.X, pos.Y, pos.Z, pos.dimension);
        var now = world.ElapsedMilliseconds;
        var calendarNow = WorldCalendarTime.Seconds(world);
        if (cache.Entries.TryGetValue(key, out var cached) && cached.Fluid == fluid.Id && cached.Kind == kind
            && now - cached.At is >= 0 and < 10000 && calendarNow - cached.CalendarAt is >= 0 and < 300)
            return cached.Batch;
        var groundwater = kind == "groundwater";
        var climate = accessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        var rainfallIndex = climate is { Rainfall: var rain } && float.IsFinite(rain) ? Math.Clamp(rain, 0, 1) : 0.5;
        GroundwaterState? aquifer = null;
        if (groundwater && GroundwaterStores.TryGetValue(world, out var store)) aquifer = store.Query(pos);
        var ambient = climate is { Temperature: var t } && float.IsFinite(t) ? Math.Clamp(t, -80, 80) : 15;
        var fallback = groundwater ? Seraph.Core.Framework.WaterCycleModel.DeriveCell(world.Seed, pos.X, pos.Y, pos.Z,
            rainfallIndex, coastal: false, ambient) : default;
        var aquiferSalt = groundwater ? (aquifer?.SalinityGPerL ?? fallback.SalinityGPerL) : 0;
        // Native hot springs specify 90 C here, independently of outdoor climate.
        var sourceTemperature = fluid.Attributes?["coolingMediumTemperature"].AsDouble(double.NaN) ?? double.NaN;
        var initial = new WaterBatch { VolumeL = 1000, TemperatureC = double.IsFinite(sourceTemperature)
                ? Math.Clamp(sourceTemperature, -80, 110) : groundwater ? (aquifer?.TemperatureC ?? fallback.TemperatureC) : ambient,
            Source = kind,
            SaltGramsPerL = salt ? 35 : aquiferSalt,
            EntericHazardPerL = config.FreshWaterHazardPerL,
            Turbidity = groundwater ? .05 : .15 };
        initial = SurfaceWater.Apply(initial, kind, config.SurfaceWater);
        var result = ApplyPollution(world, pos, initial, config, groundwater, salt);
        if (cache.Entries.Count >= 2048) cache.Entries.Clear();
        cache.Entries[key] = (now, calendarNow, fluid.Id, kind, result);
        return result;
    }
}

internal static class StoredWaterCooling
{
    // An exposed vessel approaches local air temperature over about half a game day.
    // This avoids the vanilla item's rapid cooling toward absolute zero while still
    // letting stored water respond to weather and room temperature.
    internal const double HalfLifeHours = 12;

    internal static double Advance(double waterC, double ambientC, double elapsedSeconds)
    {
        if (!double.IsFinite(waterC) || !double.IsFinite(ambientC) || !double.IsFinite(elapsedSeconds)
            || elapsedSeconds <= 0) return waterC;
        var retainedDifference = Math.Pow(.5, elapsedSeconds / (HalfLifeHours * 3600));
        return Math.Clamp(ambientC + (waterC - ambientC) * retainedDifference, -80, 110);
    }
}

internal static class WaterMetadata
{
    internal const string Key = "vsslssmod:water-v1";
    internal const string TemperatureUpdatedAtKey = "vsslssmod:water-temperature-updated-at";
    public static bool HasTag(ItemStack liquid) => liquid.Attributes.HasAttribute(Key);
    public static WaterBatch? Read(ItemStack liquid, double actualVolume)
    {
        var json = liquid.Attributes.GetString(Key, "");
        if (json.Length == 0 && !HasTag(liquid)) return null;
        if (json.Length is 0 or > 8192) throw new InvalidDataException("Invalid stored water metadata.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var batch = WaterQualityCompatibility.Read(document.RootElement);
            var result = batch is null ? null : batch with { VolumeL = actualVolume };
            return result?.Validate() is null ? result : null;
        }
        catch (JsonException ex) { throw new InvalidDataException("Unsupported stored water format; original metadata preserved.", ex); }
    }
    public static void Write(ItemStack liquid, WaterBatch batch)
    {
        if (batch.Validate() is not null) throw new ArgumentException("Invalid liquid metadata.");
        if (HasTag(liquid)) _ = Read(liquid, 0); // Reject overwriting an unsupported stored batch.
        liquid.Attributes.SetString(Key, JsonSerializer.Serialize(batch with { VolumeL = 0 }));
    }

    public static void WriteAt(IWorldAccessor world, ItemStack liquid, WaterBatch batch)
    {
        if (batch.Validate() is not null) throw new ArgumentException("Invalid liquid metadata.");
        liquid.Collectible.SetTemperature(world, liquid, (float)batch.TemperatureC, delayCooldown: false);
        // SetTemperature's heating hook may run; the mixed/transferred batch is authoritative.
        Write(liquid, batch);
        StampTemperature(world, liquid);
    }

    internal static void StampTemperature(IWorldAccessor world, ItemStack liquid)
    {
        if (TryCurrentTime(world, out var now)) liquid.Attributes.SetDouble(TemperatureUpdatedAtKey, now);
    }

    internal static bool TryCurrentTime(IWorldAccessor world, out double now)
    {
        try
        {
            // Store the absolute world-calendar clock, not WorldCalendarTime's
            // in-memory elapsed counter, so cooling continues correctly after reload.
            var totalHours = world.Calendar?.TotalHours ?? double.NaN;
            now = totalHours * 3600;
            return double.IsFinite(totalHours) && double.IsFinite(now);
        }
        catch
        {
            // Some startup states and lightweight game test worlds expose an incomplete
            // calendar proxy. Preserve stored temperature until its clock is available.
            now = 0;
            return false;
        }
    }
}
