using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed record MicroclimateResult(EnvironmentSample Environment, bool Sheltered, double RawWind, double Rainfall,
    int MappedGarments, double? RoomAirTemperatureC, bool RoomKnown);

internal static class MicroclimateAdapter
{
    private sealed class RoomCache
    {
        internal readonly Dictionary<string, (double CalendarHours, double TemperatureC)> Temperatures = new(StringComparer.Ordinal);
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IWorldAccessor, RoomCache> RoomCaches = new();

    internal static double RoomAirTemperature(ICoreServerAPI api, BlockPos pos, double climateTemperatureC,
        SeraphServerConfig config)
    {
        var heat = NearbyHeatSourceStrength(api.World, pos);
        return SampleRoom(api, pos, climateTemperatureC, (float)heat, config)?.TemperatureC ?? climateTemperatureC;
    }

    public static MicroclimateResult? Sample(ICoreServerAPI api, IServerPlayer player, SeraphServerConfig config)
    {
        var pos = player.Entity.Pos.AsBlockPos;
        var blocks = api.World.BlockAccessor;
        if (blocks.GetChunkAtBlockPos(pos) is null) return null;
        var climate = blocks.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        if (climate is null || !float.IsFinite(climate.Temperature) || climate.Temperature is < -80 or > 80) return null;
        var wind = blocks.GetWindSpeedAt(pos);
        var raw = Math.Sqrt(wind.X * wind.X + wind.Y * wind.Y + wind.Z * wind.Z);
        if (!double.IsFinite(raw)) return null;
        var sheltered = blocks.GetRainMapHeightAt(pos) > pos.Y;
        var inventory = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
        var slots = inventory?.Take(32) ?? [];
        var (clo, mapped) = Clothing(slots, config);
        var headgearShade = HeadgearShade(slots, config);
        var wetness = player.Entity.WatchedAttributes.GetFloat("wetness");
        var wet = float.IsFinite(wetness) ? Math.Clamp(wetness, 0, 1) : 0;
        // Explicit gameplay estimates. Roof is a rain cover proxy, never a certified room or wind measurement.
        clo = Math.Clamp(clo * (1 - 0.4 * wet), 0, 10);
        var cachedHeat = player.Entity.WatchedAttributes.GetTreeAttribute("bodyTemp")?.GetFloat("nearHeatSourceStrength") ?? 0;
        // The native cache is client-oriented and can be absent or stale on the
        // server. Sample loaded nearby heat sources as a bounded fallback so a
        // fire still affects the server thermal boundary and the panel estimate.
        var sampledHeat = NearbyHeatSourceStrength(api.World, pos);
        var cache = float.IsFinite(cachedHeat) ? (double)Math.Clamp(cachedHeat, 0, 10) : 0d;
        cache = Math.Clamp(Math.Max(cache, sampledHeat), 0, 10);
        var heat = cache * config.HeatCacheToRadiantC;
        var rainfall = float.IsFinite(climate.Rainfall) ? Math.Clamp(climate.Rainfall, 0, 1) : 0;
        var room = SampleRoom(api, pos, climate.Temperature, (float)cache, config);
        var roomAir = room?.TemperatureC;
        var enclosed = room?.Enclosed == true;
        if (enclosed) sheltered = true;
        var air = roomAir ?? climate.Temperature;
        var roomHeatDelta = Math.Clamp(air - climate.Temperature, -100, 100);
        var ventilation = enclosed ? .3 : 1;
        var load = Seraph.World.WaterQuality.PollutionAt(api.World, pos);
        var pollution = PollutionModel.Levels(load);
        var regional = Seraph.World.WaterQuality.RegionAt(api.World, pos);
        var accumulation = (enclosed ? 1.7 : 1) / (1 + raw * .2);
        var environmentalContamination = Math.Max(PollutionModel.Levels(load with { Chemical = 0 }).Water, regional.SoilPathogens);
        var diseaseSources = Seraph.World.WaterQuality.DiseaseSourcesAt(api.World, pos).ToDictionary(p => p.Key, p => p.Value);
        foreach (var source in Seraph.World.WaterQuality.ConfiguredRespiratorySourcesAt(api.World, pos, config))
            diseaseSources[source.Key] = Math.Min(100, diseaseSources.GetValueOrDefault(source.Key) + source.Value);
        var shoes = inventory?.Take(32).OfType<ItemSlotCharacter>().FirstOrDefault(s => s.Type == EnumCharacterDressType.Foot)?.Itemstack;
        var footwear = shoes is null ? 0 : shoes.Collectible.GetMaxDurability(shoes) is var maximum && maximum > 0
            ? Math.Clamp(shoes.Collectible.GetRemainingDurability(shoes) / (double)maximum, 0, 1) : 1;
        if (diseaseSources.ContainsKey("tetanus")) diseaseSources["tetanus"] *= 1 - footwear;
        var waterContact = WaterImmersion.Sample(blocks, player.Entity);
        var immersion = waterContact.Source is { } waterPos
            ? Seraph.World.WaterQuality.WorldSource(api.World, waterPos, config) : null;
        var submerged = immersion is null ? 0 : waterContact.Fraction;
        if (immersion is { } water)
            foreach (var agent in water.PathogensPerL.Where(p => Pathogens.Wound(p.Key)))
                diseaseSources[agent.Key] = Math.Max(diseaseSources.GetValueOrDefault(agent.Key), agent.Value);
        // Ordinary roofs, hides and smoke do not establish a named infectious source.
        // Five local loaded-block reads, on the server thread; no world scan from tooltips.
        var vegetation = !sheltered && !player.Entity.Swimming && air is >= 12 and <= 35
            && new[] { pos, pos.AddCopy(1, 0, 0), pos.AddCopy(-1, 0, 0), pos.AddCopy(0, 0, 1), pos.AddCopy(0, 0, -1) }
                .Any(p => blocks.GetChunkAtBlockPos(p) is not null
                    && blocks.GetBlock(p).BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves);
        var humidity = HumidityModel.RelativeHumidity(climate.WorldgenRainfall, climate.Rainfall, climate.Temperature, air);
        if (!double.IsFinite(humidity)) humidity = config.DefaultRelativeHumidity;
        var daylight = 0d;
        try { daylight = Math.Clamp(api.World.Calendar.GetDayLightStrength(pos), 0, 1); }
        catch { /* Calendar lighting is optional on partially initialized worlds. */ }
        var solarExposure = SolarExposure(blocks, pos, enclosed, sheltered, headgearShade);
        var solar = config.SolarPowerWAtFullExposure * daylight * solarExposure;
        var environment = new EnvironmentSample
        {
            AirTemperatureC = Math.Clamp(air, -80, 100), RadiantTemperatureC = Math.Clamp(air + heat, -80, 100),
            AbsorbedSolarPowerW = Math.Clamp(solar, 0, 10000),
            RelativeHumidity = humidity,
            WindSpeedMps = Math.Clamp(raw * config.WindGameUnitToMps * (sheltered ? 0.3 : 1), 0, 100),
            ClothingInsulationClo = clo, ClothingEvaporativeResistanceM2PaPerW = Math.Max(1, 25 * clo),
            WaterImmersionFraction = submerged,
            ContactConductanceWPerK = WaterImmersion.Conductance(submerged, clo),
            ContactTemperatureC = immersion is null ? air : Math.Clamp(immersion.TemperatureC, -80, 100),
            RainFilmInflowKgPerSecond = sheltered || climate.Temperature < 0 ? 0 : rainfall * config.RainGameUnitToFilmKgPerSecond,
            AirPollution = Math.Clamp(Math.Max(pollution.Smoke, regional.Air.Total) * accumulation, 0, 1),
            AirbornePathogens = Math.Clamp(Math.Max(pollution.BiologicalAir, regional.AirbornePathogens) * accumulation, 0, 1),
            CarbonMonoxide = Math.Clamp(regional.Air.CO * accumulation, 0, 1),
            ToxicGas = Math.Clamp(regional.Air.Toxicity * accumulation, 0, 1),
            EnvironmentalContamination = environmentalContamination,
            RoomHeatDeltaC = roomHeatDelta,
            RoomVentilation = ventilation,
            DiseaseSources = diseaseSources,
            ColdExposureByRegion = RegionalColdExposure(inventory?.Take(32) ?? []),
            VectorSuitable = vegetation,
            WashedByEnvironment = player.Entity.Swimming || !sheltered && rainfall > .1 && climate.Temperature > 0,
            DampSkin = wet > .5,
            SourceQuality = EnvironmentSourceQuality.AllEstimated
        };
        return environment.Validate() is null ? new(environment, sheltered, raw, rainfall, mapped, roomAir, room is not null) : null;
    }

    /// <summary>Returns daylight reaching skin after local cover and headgear.</summary>
    internal static double SolarExposure(IBlockAccessor blocks, BlockPos pos, bool enclosed, bool sheltered, double headgearShade)
    {
        if (enclosed) return 0;
        var cover = sheltered || blocks.GetRainMapHeightAt(pos) > pos.Y ? .05 : 1;
        return Math.Clamp(cover * (1 - Math.Clamp(headgearShade, 0, 1)), 0, 1);
    }

    internal static (double Clo, int Mapped) Clothing(IEnumerable<ItemSlot> slots, SeraphServerConfig config)
    {
        var clo = 0d;
        var mapped = 0;
        var unknownGarment = false;
        foreach (var slot in slots.Take(32))
        {
            try
            {
                if (slot is ItemSlotCharacter { Type: EnumCharacterDressType.ArmorHead or EnumCharacterDressType.ArmorBody or EnumCharacterDressType.ArmorLegs }) continue;
                var stack = slot?.Itemstack;
                if (stack?.Collectible?.Code is not { } code) continue;
                if (config.ClothingCloByCode.TryGetValue(code.ToString(), out var layer))
                { clo += layer; mapped++; continue; }
                var attributes = stack.ItemAttributes;
                var category = attributes?["clothescategory"].AsString();
                if (Enum.TryParse<EnumCharacterDressType>(category, true, out var dress)
                    && dress is EnumCharacterDressType.ArmorHead or EnumCharacterDressType.ArmorBody or EnumCharacterDressType.ArmorLegs) continue;
                if (attributes?["warmth"].Exists == true)
                {
                    var warmth = attributes["warmth"].AsDouble(double.NaN);
                    var condition = stack.Attributes.GetFloat("condition", 1);
                    if (double.IsFinite(warmth) && warmth >= 0 && float.IsFinite(condition))
                    {
                        // Same condition curve as native wearable warmth, without initializing or wearing the item.
                        clo += .1 * Math.Min(warmth, Math.Clamp(condition, 0, 1) * 2 * warmth);
                        mapped++;
                        continue;
                    }
                }
                if (!string.IsNullOrWhiteSpace(category)) unknownGarment = true;
            }
            catch { /* A third-party slot must not invalidate the remaining clothing sample. */ }
        }
        // One fallback allowance for unknown garments, never for an empty/naked character.
        if (unknownGarment) clo += config.DefaultClothingClo;
        return (Math.Clamp(clo, 0, 10), mapped);
    }

    internal static double HeadgearShade(IEnumerable<ItemSlot> slots, SeraphServerConfig config)
    {
        var shade = 0d;
        foreach (var slot in slots.Take(32))
        {
            try
            {
                var character = slot as ItemSlotCharacter;
                var stack = slot?.Itemstack;
                if (stack?.Collectible?.Code is not { } code || !IsHeadgear(character, stack)) continue;
                var value = config.HeadgearShadeByCode.TryGetValue(code.ToString(), out var mapped)
                    ? mapped : AttributeShade(stack);
                if (double.IsFinite(value)) shade = Math.Max(shade, Math.Clamp(value, 0, 1));
            }
            catch { /* A third-party head slot must not invalidate climate sampling. */ }
        }
        return shade;
    }

    private static bool IsHeadgear(ItemSlotCharacter? character, ItemStack stack)
    {
        if (character?.Type is EnumCharacterDressType.Head or EnumCharacterDressType.Face or EnumCharacterDressType.ArmorHead)
            return true;
        var category = stack.ItemAttributes?["clothescategory"].AsString();
        return Enum.TryParse<EnumCharacterDressType>(category, true, out var dress)
            && dress is EnumCharacterDressType.Head or EnumCharacterDressType.Face or EnumCharacterDressType.ArmorHead;
    }

    private static double AttributeShade(ItemStack stack)
    {
        var attributes = stack.ItemAttributes;
        foreach (var key in new[] { "shade", "sunshade", "sunShade" })
        {
            if (attributes?[key].Exists != true) continue;
            var value = attributes[key].AsDouble(double.NaN);
            if (double.IsFinite(value)) return value;
        }
        return 0;
    }

    private static IReadOnlyDictionary<string, double> RegionalColdExposure(IEnumerable<ItemSlot> slots)
    {
        var covered = new HashSet<EnumCharacterDressType>();
        foreach (var slot in slots.Take(32).OfType<ItemSlotCharacter>())
        {
            if (slot.Itemstack is null) continue;
            covered.Add(slot.Type);
        }
        bool Has(params EnumCharacterDressType[] types) => types.Any(covered.Contains);
        var head = Has(EnumCharacterDressType.Head, EnumCharacterDressType.ArmorHead, EnumCharacterDressType.Face);
        var upper = Has(EnumCharacterDressType.UpperBody, EnumCharacterDressType.UpperBodyOver,
            EnumCharacterDressType.ArmorBody, EnumCharacterDressType.Neck, EnumCharacterDressType.Shoulder);
        var arms = Has(EnumCharacterDressType.Arm, EnumCharacterDressType.Hand, EnumCharacterDressType.UpperBody,
            EnumCharacterDressType.UpperBodyOver, EnumCharacterDressType.ArmorBody, EnumCharacterDressType.Shoulder);
        var legs = Has(EnumCharacterDressType.LowerBody, EnumCharacterDressType.ArmorLegs);
        var feet = Has(EnumCharacterDressType.Foot, EnumCharacterDressType.LowerBody, EnumCharacterDressType.ArmorLegs);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["head-neck"] = head ? .25 : 1,
            ["chest"] = upper ? .2 : 1,
            ["abdomen"] = upper ? .3 : 1,
            ["left-arm"] = arms ? .25 : 1, ["right-arm"] = arms ? .25 : 1,
            ["left-leg"] = legs ? .25 : 1, ["right-leg"] = legs ? .25 : 1,
            ["left-foot"] = feet ? .2 : 1, ["right-foot"] = feet ? .2 : 1
        };
    }

    private static double NearbyHeatSourceStrength(IWorldAccessor world, BlockPos center)
    {
        var blocks = world.BlockAccessor;
        var strength = 0d;
        for (var dx = -4; dx <= 4; dx++)
        for (var dy = -2; dy <= 2; dy++)
        for (var dz = -4; dz <= 4; dz++)
        {
            var pos = center.AddCopy(dx, dy, dz);
            if (blocks.GetChunkAtBlockPos(pos) is null) continue;
            if (blocks.GetBlockEntity(pos) is not IHeatSource source) continue;
            try { strength += Math.Max(0, source.GetHeatStrength(world, pos, center)); }
            catch { /* An incompatible third-party heat source is ignored. */ }
            if (strength >= 10) return 10;
        }
        return Math.Clamp(strength, 0, 10);
    }

    private static (double TemperatureC, bool Enclosed)? SampleRoom(ICoreServerAPI api, BlockPos pos,
        double climateTemperatureC, float heatSourceStrength, SeraphServerConfig config)
    {
        try
        {
            var registry = api.ModLoader.GetModSystem<RoomRegistry>();
            var room = registry.GetRoomForPosition(pos);
            if (room?.Location is null) return null;
            var enclosed = room.ExitCount == 0 || room.SkylightCount < room.NonSkylightCount;
            if (!enclosed) return (climateTemperatureC, false);
            var location = room.Location;
            var key = $"{pos.dimension}:{location.X1},{location.Y1},{location.Z1}:{location.X2},{location.Y2},{location.Z2}";
            var volume = Math.Max(1, (double)(location.X2 - location.X1 + 1)
                * (location.Y2 - location.Y1 + 1) * (location.Z2 - location.Z1 + 1));
            var now = api.World.Calendar.TotalHours;
            var cache = RoomCaches.GetValue(api.World, static _ => new RoomCache());
            if (!cache.Temperatures.TryGetValue(key, out var previous))
            {
                cache.Temperatures[key] = (now, climateTemperatureC);
                return (climateTemperatureC, true);
            }
            var dt = Math.Clamp(now - previous.CalendarHours, 0, 24);
            var inputs = new RoomThermalInputs(climateTemperatureC, float.IsFinite(heatSourceStrength)
                ? Math.Clamp(heatSourceStrength, 0, 10) : 0, true, Math.Max(0, room.CoolingWallCount),
                Math.Max(0, room.NonCoolingWallCount), volume, dt);
            var temperature = RoomTemperatureModel.Advance(previous.TemperatureC, inputs, config.RoomHeatHalfLifeHours,
                config.RoomHeatMaxBonusC, config.RoomHeatCelsiusPerStrength);
            if (!double.IsFinite(temperature)) return null;
            cache.Temperatures[key] = (now, temperature);
            if (cache.Temperatures.Count > 1024) cache.Temperatures.Clear();
            return (Math.Clamp(temperature, -80, 100), true);
        }
        catch
        {
            // Room lookup is an optional observation. A missing or incompatible room provider
            // must leave the original climate sample usable.
            return null;
        }
    }
}
