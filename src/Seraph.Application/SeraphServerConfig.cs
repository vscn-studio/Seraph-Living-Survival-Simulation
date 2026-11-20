using Seraph.Core.Simulation;
using Seraph.Core.Framework;

namespace Seraph.Application;

public sealed record SeraphServerConfig
{
    // Legacy config key now gates only the single closed-eye yaw change.
    public bool EnableDrowsyMovement { get; init; } = true;
    public bool EnableClinicalEffects { get; init; } = true;
    public bool EnableSymptomVisuals { get; init; } = true;
    public double WoundInfectionHazardPerSecond { get; init; } = 0.00005;
    public double RespiratoryTransmissionUnitsPerSecond { get; init; } = 0.005;
    public Dictionary<string, double> RespiratoryProtectionByCode { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> RespiratorySourcesByBlockCode { get; init; } = new(StringComparer.Ordinal);
    public double BathWaterCoolingHalfLifeSeconds { get; init; } = 5400;
    public double BathWaterThermalKillPerDegreeSecond { get; init; } = 0.00002;
    public double HandToFoodTransferFraction { get; init; } = .2;
    public double CleanWaterHandRetention { get; init; } = .1;
    public double DirtyWaterHandRetention { get; init; } = .5;
    public DiseaseExposureOptions DiseaseExposure { get; init; } = new();
    public SurfaceWaterOptions SurfaceWater { get; init; } = new();
    public SanitationOptions Sanitation { get; init; } = new();
    public ThermalAdaptationOptions ThermalAdaptation { get; init; } = new();
    public PainOptions Pain { get; init; } = new();
    public CirculationOptions Circulation { get; init; } = new();
    public ExertionOptions Exertion { get; init; } = new();
    // BP 2.16.8 reference throughput. Protein uses a separate slow quota below.
    public double DigestionGPerGameHour { get; init; } = 1667;
    public DigestionOptions Digestion { get; init; } = new();
    public double BasalWaterLossGPerGameHour { get; init; } = 75;
    public double UrineProductionMlPerGameHour { get; init; } = 60;
    public double EntericWaterLossLPerHour { get; init; } = 0.5;
    public double ClinicalDamageMultiplier { get; init; } = 1;
    public bool EnableInjuryActionEffects { get; init; } = true;
    [System.Text.Json.Serialization.JsonRequired]
    public string Format { get; init; } = "seraph.release.config";
    [System.Text.Json.Serialization.JsonRequired]
    public int ConfigVersion { get; init; } = 1;
    // Optional explicit game hours override the corresponding normalized game-second setting.
    public double? SleepPressureRiseGameHours { get; init; }
    public double? SleepPressureRecoveryGameHours { get; init; }
    public double? FatigueRiseGameHours { get; init; }
    public double? FatigueSleepRecoveryGameHours { get; init; }
    public double? FatigueSeatedRecoveryGameHours { get; init; }
    public SeraphServerConfig ResolveUnits() => this with {
        SleepPressureRiseSeconds = SleepPressureRiseGameHours is { } rise ? rise * 3600 : SleepPressureRiseSeconds,
        SleepPressureRecoverySeconds = SleepPressureRecoveryGameHours is { } sleep ? sleep * 3600 : SleepPressureRecoverySeconds,
        FatigueRiseSeconds = FatigueRiseGameHours is { } fatigue ? fatigue * 3600 : FatigueRiseSeconds,
        FatigueSleepRecoverySeconds = FatigueSleepRecoveryGameHours is { } recovery ? recovery * 3600 : FatigueSleepRecoverySeconds,
        FatigueSeatedRecoverySeconds = FatigueSeatedRecoveryGameHours is { } seated ? seated * 3600 : FatigueSeatedRecoverySeconds };
    public string Mode { get; init; } = "survival";
    public double PhysiologySpeed { get; init; } = 1;
    public double FatigueRiseSeconds { get; init; } = 172800;
    public double FatigueSleepRecoverySeconds { get; init; } = 28800;
    public double FatigueSeatedRecoverySeconds { get; init; } = 28800 / .385;
    public double SleepPressureRiseSeconds { get; init; } = 90000;
    public double SleepPressureRecoverySeconds { get; init; } = 28800;
    // Bed sleep only: retain quality differences and seated/K-rest pacing.
    public double BedSleepPressureRecoveryMultiplier { get; init; } = 2;
    public bool TelemetryEnabled { get; init; }
    public int CheckpointSeconds { get; init; } = 30;
    public bool EnableMovementEffects { get; init; } = true;
    public bool EnableWoundTracking { get; init; } = true;
    public bool EnableGroundWaterDrinking { get; init; } = true;
    public bool EnableMedicalActions { get; init; } = true;
    // Disabled by default: received saturation is a game unit, not verified food mass or energy.
    public bool EnableEstimatedFoodEnergy { get; init; }
    public double EstimatedKJPerSaturation { get; init; } = 8;
    public double DrinkServingL { get; init; } = 0.25;
    public double DrinkCooldownRealSeconds { get; init; } = 3;
    public bool EnableDefecation { get; init; } = true;
    // Game balance conversion from confirmed received saturation; no food mass is inferred.
    public double BowelUnitsPerSaturation { get; init; } = 0.2;
    public double FreshWaterHazardPerL { get; init; } = 0.02;
    public double RainWaterHazardPerL { get; init; } = 0.005;
    public double FecalWaterHazardPerL { get; init; } = 0.5;
    // Exact codes or terminal-* patterns for additional pack-specific local pollution sources.
    public Dictionary<string, double> WaterPollutionHazardPerLByBlockCode { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> AnimalWastePathogens { get; init; } = new() { ["ecoli"] = .6, ["salmonella"] = .4 };
    public Dictionary<string, double> CarrionPathogens { get; init; } = new() { ["salmonella"] = .7, ["wound-staph"] = .3 };
    public double DefaultRelativeHumidity { get; init; } = 0.5;
    public double DefaultClothingClo { get; init; } = 0.6;
    public double WindGameUnitToMps { get; init; } = 1;
    // Gameplay conversion: a nearby burning native firepit (strength 10) adds 40 C radiant warmth.
    public double HeatCacheToRadiantC { get; init; } = 4;
    // Solar load at full daylight and unobstructed exposure. The thermal core
    // treats this as absorbed skin power, so the value is intentionally bounded
    // to keep a bad server config from destabilizing the heat solver.
    public double SolarPowerWAtFullExposure { get; init; } = 500;
    public double RoomHeatMaxBonusC { get; init; } = 18;
    public double RoomHeatCelsiusPerStrength { get; init; } = 1.8;
    public double RoomHeatHalfLifeHours { get; init; } = 4;
    public double RainGameUnitToFilmKgPerSecond { get; init; } = 0.0001;
    public Dictionary<string, double> ClothingCloByCode { get; init; } = new(StringComparer.Ordinal);
    // Values are fractions of overhead solar exposure blocked by a head item.
    // Unknown headwear remains warmth-only until it is listed here or carries a
    // matching shade/sunshade attribute in its item definition.
    public Dictionary<string, double> HeadgearShadeByCode { get; init; } = new(StringComparer.Ordinal);
    public int CsvMaximumMiB { get; init; } = 16;

    public string? Validate() => SurfaceWater is null || !SurfaceWater.Valid ? "Invalid surface water configuration."
        : ThermalAdaptation is null || ThermalAdaptation.Validate() is not null ? "Invalid thermal adaptation configuration."
        : Pain is null || Pain.Validate() is not null ? "Invalid pain configuration."
        : DiseaseExposure is null || DiseaseExposure.Validate() is not null ? "Invalid disease exposure configuration."
        : Sanitation is null || Sanitation.Validate() is not null ? "Invalid sanitation configuration."
        : Circulation is null || Circulation.Validate() is not null ? "Invalid circulation configuration."
        : Exertion is null || Exertion.Validate() is not null ? "Invalid exertion recovery configuration."
        : Digestion is null || Digestion.Validate() is not null ? "Invalid digestion configuration."
        : !double.IsFinite(DigestionGPerGameHour) || DigestionGPerGameHour is < 1 or > 10000 ? "Invalid digestion rate in g/h."
        : !double.IsFinite(BasalWaterLossGPerGameHour) || BasalWaterLossGPerGameHour is < 0 or > 10000 ? "Invalid baseline water loss in g/game hour."
        : !double.IsFinite(BedSleepPressureRecoveryMultiplier) || BedSleepPressureRecoveryMultiplier is < 1 or > 8 ? "Invalid bed sleep pressure recovery multiplier."
        : Format != "seraph.release.config" || ConfigVersion != 1 ? "Unsupported release configuration format version."
        : Mode is not ("survival" or "shadow" or "observe") ? "Mode must be survival, shadow or observe."
        : !double.IsFinite(PhysiologySpeed) || PhysiologySpeed is < SchedulerOptions.MinimumPhysiologySpeed or > SchedulerOptions.MaximumPhysiologySpeed ? "PhysiologySpeed must be finite and between 0.1 and 20."
        : !double.IsFinite(WoundInfectionHazardPerSecond) || WoundInfectionHazardPerSecond is < 0 or > .01
            || !double.IsFinite(RespiratoryTransmissionUnitsPerSecond) || RespiratoryTransmissionUnitsPerSecond is < 0 or > 1
            || !double.IsFinite(BathWaterCoolingHalfLifeSeconds) || BathWaterCoolingHalfLifeSeconds is < 60 or > 604800
            || !double.IsFinite(BathWaterThermalKillPerDegreeSecond) || BathWaterThermalKillPerDegreeSecond is < 0 or > .01
            || new[] { HandToFoodTransferFraction, CleanWaterHandRetention, DirtyWaterHandRetention }.Any(x => !double.IsFinite(x) || x is < 0 or > 1)
            || !double.IsFinite(EntericWaterLossLPerHour) || EntericWaterLossLPerHour is < 0 or > 10
            || !double.IsFinite(ClinicalDamageMultiplier) || ClinicalDamageMultiplier is < 0 or > 10 ? "Invalid clinical rates."
        : CheckpointSeconds is < 10 or > 300 ? "CheckpointSeconds must be between 10 and 300."
        : new[] { SleepPressureRiseGameHours, SleepPressureRecoveryGameHours, FatigueRiseGameHours, FatigueSleepRecoveryGameHours, FatigueSeatedRecoveryGameHours }.Any(x => x.HasValue && (!double.IsFinite(x.Value) || x.Value is < (1d / 60) or > 27777)) ? "Invalid rest pacing constants."
        : new[] { FatigueRiseSeconds, SleepPressureRiseSeconds }.Any(x => !double.IsFinite(x) || x is < 60 or > 1e8)
            || new[] { FatigueSleepRecoverySeconds, FatigueSeatedRecoverySeconds, SleepPressureRecoverySeconds }.Any(x => !double.IsFinite(x) || x is < 30 or > 1e8) ? "Invalid rest pacing constants."
        : !double.IsFinite(DrinkServingL) || DrinkServingL is < 0.01 or > 1 ? "Invalid drinking serving."
        : !double.IsFinite(DrinkCooldownRealSeconds) || DrinkCooldownRealSeconds is < 1 or > 60 ? "Invalid drinking cooldown."
        : !double.IsFinite(BowelUnitsPerSaturation) || BowelUnitsPerSaturation is < 0 or > 2 ? "Invalid bowel game conversion."
        : new[] { FreshWaterHazardPerL, RainWaterHazardPerL, FecalWaterHazardPerL }.Any(x => !double.IsFinite(x) || x is < 0 or > 100) ? "Invalid water hazard preset."
        : !PathogenLoads.Valid(AnimalWastePathogens) || !PathogenLoads.Valid(CarrionPathogens) ? "Invalid pathogen source profiles."
        : WaterPollutionHazardPerLByBlockCode is null || WaterPollutionHazardPerLByBlockCode.Count > 256
            || WaterPollutionHazardPerLByBlockCode.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 128
                || !kv.Key.Contains(':') || !double.IsFinite(kv.Value) || kv.Value is < 0 or > 100) ? "Invalid water pollution source map."
        : !double.IsFinite(EstimatedKJPerSaturation) || EstimatedKJPerSaturation is < 0 or > 100 ? "Invalid estimated food energy mapping."
        : !double.IsFinite(DefaultRelativeHumidity) || DefaultRelativeHumidity is < 0 or > 1 ? "Invalid estimated humidity."
        : !double.IsFinite(DefaultClothingClo) || DefaultClothingClo is < 0 or > 10 ? "Invalid default clothing insulation."
        : !double.IsFinite(WindGameUnitToMps) || WindGameUnitToMps is < 0 or > 20 ? "Invalid estimated wind mapping."
        : !double.IsFinite(HeatCacheToRadiantC) || HeatCacheToRadiantC is < 0 or > 30 ? "Invalid estimated heat mapping."
        : !double.IsFinite(SolarPowerWAtFullExposure) || SolarPowerWAtFullExposure is < 0 or > 2000 ? "Invalid solar heat mapping."
        : !double.IsFinite(RoomHeatMaxBonusC) || RoomHeatMaxBonusC is < 0 or > 50
            || !double.IsFinite(RoomHeatCelsiusPerStrength) || RoomHeatCelsiusPerStrength is < 0 or > 10
            || !double.IsFinite(RoomHeatHalfLifeHours) || RoomHeatHalfLifeHours is < 0.1 or > 48 ? "Invalid room heat mapping."
        : !double.IsFinite(RainGameUnitToFilmKgPerSecond) || RainGameUnitToFilmKgPerSecond is < 0 or > 0.01 ? "Invalid estimated rain mapping."
        : ClothingCloByCode is null || ClothingCloByCode.Count > 2048 || ClothingCloByCode.Any(kv => string.IsNullOrWhiteSpace(kv.Key)
            || kv.Key.Length > 128 || !double.IsFinite(kv.Value) || kv.Value is < 0 or > 10) ? "Invalid clothing map."
        : HeadgearShadeByCode is null || HeadgearShadeByCode.Count > 2048 || HeadgearShadeByCode.Any(kv => string.IsNullOrWhiteSpace(kv.Key)
            || kv.Key.Length > 128 || !double.IsFinite(kv.Value) || kv.Value is < 0 or > 1) ? "Invalid headgear shade map."
        : RespiratoryProtectionByCode is null || RespiratoryProtectionByCode.Count > 256 || RespiratoryProtectionByCode.Any(kv => string.IsNullOrWhiteSpace(kv.Key)
            || kv.Key.Length > 128 || !double.IsFinite(kv.Value) || kv.Value is < 0 or > 1) ? "Invalid respiratory protection map."
        : RespiratorySourcesByBlockCode is null || RespiratorySourcesByBlockCode.Count > 256 || RespiratorySourcesByBlockCode.Any(kv => string.IsNullOrWhiteSpace(kv.Key)
            || kv.Key.Length > 128 || !kv.Key.Contains(':') || !double.IsFinite(kv.Value) || kv.Value is <= 0 or > 1) ? "Invalid respiratory source map."
        : CsvMaximumMiB is < 1 or > 128 ? "Invalid CSV budget."
        : null;
}

public static class TargetVersion
{
    // Mod dependency versions are minimums, so the minor-version boundary is also checked at runtime.
    public static bool IsSupported(string? version) => version is not null
        && version.Split('.', '-', '+') is { Length: >= 3 } parts
        && parts[0] == "1" && parts[1] == "22"
        && int.TryParse(parts[2], out var patch) && patch >= 0;
}

public sealed class SettingsAuthority
{
    public SeraphServerConfig Config { get; private set; }
    public long Revision { get; private set; } = 1;

    public SettingsAuthority(SeraphServerConfig config)
    {
        if (config.Validate() is { } error) throw new ArgumentException(error, nameof(config));
        Config = config;
    }

    public string? ValidateChange(bool authorized, long expectedRevision, double speed)
        => !authorized ? "Administrator permission required."
        : expectedRevision != Revision ? "Settings changed. Refresh and retry."
        : (Config with { PhysiologySpeed = speed }).Validate();

    public void Commit(double speed, bool telemetry)
    {
        var next = Config with { PhysiologySpeed = speed, TelemetryEnabled = telemetry };
        if (next.Validate() is { } error) throw new ArgumentException(error, nameof(speed));
        Config = next;
        Revision = checked(Revision + 1);
    }
    public void CommitMode(string mode)
    {
        var next = Config with { Mode = mode };
        if (next.Validate() is { } error) throw new ArgumentException(error, nameof(mode));
        Config = next;
        Revision = checked(Revision + 1);
    }
}
