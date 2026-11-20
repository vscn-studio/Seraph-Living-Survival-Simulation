using ProtoBuf;

namespace Seraph.Network;

public static class Protocol
{
    public const int Version = 1;
    public const string Channel = "vsslssmod-release-v1";
}

[ProtoContract]
public sealed class OrganicMistParticle
{
    [ProtoMember(1)] public double X { get; set; }
    [ProtoMember(2)] public double Y { get; set; }
    [ProtoMember(3)] public double Z { get; set; }
    [ProtoMember(4)] public float Vx { get; set; }
    [ProtoMember(5)] public float Vy { get; set; }
    [ProtoMember(6)] public float Vz { get; set; }
}

/// <summary>Receipts from native callbacks that execute only on the owning client.</summary>
[ProtoContract]
public sealed class NativeWorkReceipt
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public long Sequence { get; set; }
    [ProtoMember(3)] public bool Attack { get; set; }
    [ProtoMember(4)] public double Seconds { get; set; }
    [ProtoMember(5)] public int ToolId { get; set; }
    [ProtoMember(6)] public int X { get; set; }
    [ProtoMember(7)] public int Y { get; set; }
    [ProtoMember(8)] public int Z { get; set; }
    [ProtoMember(9)] public int Dimension { get; set; }
    [ProtoMember(10)] public int BlockId { get; set; }
    [ProtoMember(11)] public int Life { get; set; }
}

[ProtoContract]
public sealed class WaterInfoRequest
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public int X { get; set; }
    [ProtoMember(3)] public int Y { get; set; }
    [ProtoMember(4)] public int Z { get; set; }
    [ProtoMember(5)] public int Dimension { get; set; }
    [ProtoMember(6)] public long RequestId { get; set; }
}
[ProtoContract]
public sealed class WaterInfoResponse
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public WaterHoverSnapshot? Water { get; set; }
    [ProtoMember(3)] public string FailureReason { get; set; } = "";
    [ProtoMember(4)] public long RequestId { get; set; }
    [ProtoMember(5)] public long Revision { get; set; }
    [ProtoMember(6)] public WaterHoverSnapshot[] Nearby { get; set; } = [];
}

/// <summary>Short-lived, server-authorized visual reveal for a virtual aquifer.</summary>
[ProtoContract]
public sealed class GroundwaterReveal
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public int X { get; set; }
    [ProtoMember(3)] public int Y { get; set; }
    [ProtoMember(4)] public int Z { get; set; }
    [ProtoMember(5)] public int Dimension { get; set; }
    [ProtoMember(6)] public float SurfaceY { get; set; }
    [ProtoMember(7)] public float Radius { get; set; } = 8;
    [ProtoMember(8)] public int DurationMs { get; set; } = 15000;
    [ProtoMember(9)] public bool ExcavationExposure { get; set; }
    // Server-authorized saturated wall faces around the newly opened air cell.
    // Bits: north, south, west, east.  The client never infers exposure from
    // its local block state.
    [ProtoMember(10)] public byte SaturatedFaceMask { get; set; }
    [ProtoMember(11)] public float WaterContent { get; set; }
}
[ProtoContract]
public sealed class FoodCatalogMessage
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public string Json { get; set; } = "";
}

[ProtoContract]
public sealed class ClientHello
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = Protocol.Version;
    [ProtoMember(2)] public bool RequestDiagnostics { get; set; }
}

[ProtoContract]
public sealed class ServerSnapshot
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = Protocol.Version;
    [ProtoMember(2)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(3)] public long Sequence { get; set; }
    [ProtoMember(4)] public string Summary { get; set; } = "";
    [ProtoMember(5)] public bool CanViewDiagnostics { get; set; }
    [ProtoMember(6)] public bool CanEditSettings { get; set; }
    [ProtoMember(7)] public long SettingsRevision { get; set; }
    [ProtoMember(8)] public double PhysiologySpeed { get; set; }
    [ProtoMember(9)] public bool TelemetryEnabled { get; set; }
    [ProtoMember(10)] public string[] PhysiologyLines { get; set; } = [];
    [ProtoMember(11)] public string[] EnvironmentLines { get; set; } = [];
    [ProtoMember(12)] public string[] ClockLines { get; set; } = [];
    [ProtoMember(13)] public string[] QualityLines { get; set; } = [];
    [ProtoMember(14)] public string[] PublicLines { get; set; } = [];
    // Self-only character panel projection. These are never populated from another player.
    [ProtoMember(15)] public string[] PanelStatusLines { get; set; } = [];
    [ProtoMember(16)] public string[] PanelTemperatureLines { get; set; } = [];
    [ProtoMember(17)] public string[] PanelNutritionLines { get; set; } = [];
    [ProtoMember(18)] public string[] PanelRecoveryLines { get; set; } = [];
    [ProtoMember(19)] public string[] PanelHydrationLines { get; set; } = [];
    [ProtoMember(20)] public string[] PanelWoundLines { get; set; } = [];
    [ProtoMember(21)] public BodyRegionSnapshot[] BodyRegions { get; set; } = [];
    [ProtoMember(22)] public HudGauge[] Gauges { get; set; } = [];
    [ProtoMember(23)] public string[] WaterLines { get; set; } = [];
    [ProtoMember(24)] public bool HasUnlocatedInjury { get; set; }
    [ProtoMember(25)] public string[] InteractionLines { get; set; } = [];
    [ProtoMember(26)] public WaterHoverSnapshot? WaterHover { get; set; }
    [ProtoMember(27)] public WorldActionProgress? ActionProgress { get; set; }
    [ProtoMember(28)] public NutrientGauge[] Nutrients { get; set; } = [];
    // Projected total contents: non-water food mass plus actual unabsorbed water.
    [ProtoMember(29)] public double GutMassG { get; set; }
    [ProtoMember(30)] public double GutMaximumG { get; set; } = 5000;
    [ProtoMember(31)] public double GutWaterL { get; set; }
    [ProtoMember(32)] public double BowelUnits { get; set; }
    [ProtoMember(33)] public double EnergyKJ { get; set; }
    [ProtoMember(34)] public double EnergyMaximumKJ { get; set; }
    [ProtoMember(35)] public double GutEnergyKJ { get; set; }
    [ProtoMember(36)] public double GutEnergyMaximumKJ { get; set; }
    [ProtoMember(37)] public PanelMetric[] StatusMetrics { get; set; } = [];
    [ProtoMember(38)] public PanelMetric[] TemperatureMetrics { get; set; } = [];
    [ProtoMember(39)] public PanelMetric[] RecoveryMetrics { get; set; } = [];
    [ProtoMember(40)] public StatusIconSnapshot[] StatusIcons { get; set; } = [];
    [ProtoMember(41)] public double GlycogenEnergyKJ { get; set; }
    [ProtoMember(42)] public double GlycogenCapacityKJ { get; set; }
    [ProtoMember(43)] public double FatEnergyKJ { get; set; }
    [ProtoMember(44)] public double FatCapacityKJ { get; set; }
    [ProtoMember(45)] public FunctionSnapshot? Functions { get; set; }
    [ProtoMember(46)] public ClinicalEffectSnapshot[] ClinicalEffects { get; set; } = [];
    [ProtoMember(47)] public bool SymptomVisuals { get; set; }
    [ProtoMember(48)] public bool OwnWoundCare { get; set; }
    [ProtoMember(49)] public double SleepPressure { get; set; }
    [ProtoMember(50)] public bool IsSleeping { get; set; }
    [ProtoMember(51)] public double SleepBlurSeverity { get; set; }
    [ProtoMember(52)] public double AlertnessReliefSeconds { get; set; }
    // Field 53 retired: continuous drowsy locomotion clock; never reuse.
    // Historical wire name: permission for a single yaw change on full eye closure.
    // This is a presentation permission; it never authorizes a dream route or input rewrite.
    [ProtoMember(54)] public bool DrowsyMovement { get; set; }
    [ProtoMember(55)] public PanelMetric[] FunctionMetrics { get; set; } = [];
    [ProtoMember(56)] public PanelMetric[] DebugValues { get; set; } = [];
    [ProtoMember(57)] public long LifeGeneration { get; set; }
    [ProtoMember(58)] public bool IsClosedEyeResting { get; set; }
    [ProtoMember(59)] public BedRestSnapshot? BedRest { get; set; }
    [ProtoMember(60)] public double Adrenaline { get; set; }
    [ProtoMember(61)] public double CleanRemainingGameHours { get; set; }
    [ProtoMember(62)] public double BathedRemainingGameHours { get; set; }
    [ProtoMember(63)] public int HydrationStage { get; set; }
    // water, carbohydrate, protein, fat, fiber, indigestible matter; exact server grams.
    [ProtoMember(64)] public double[] StomachCompositionG { get; set; } = [];
    [ProtoMember(65)] public PressureStatus? Pressure { get; set; }
    [ProtoMember(66)] public ExertionState? Exertion { get; set; }
    [ProtoMember(67)] public double EffectiveHeartRateBpm { get; set; } = 72;
    [ProtoMember(68)] public double VentilationMultiplier { get; set; } = 1;
    [ProtoMember(69)] public SleepEyeSnapshot? SleepEyes { get; set; }
    [ProtoMember(70)] public PollutionDebugVolume[] PollutionDebugVolumes { get; set; } = [];
}

public enum PollutionDebugKind : byte { Source, RestrictedSource, SealedSource, WaterPath, AirPath, Barrier }

[ProtoContract]
public sealed class PollutionDebugVolume
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Dimension { get; set; }
    [ProtoMember(5)] public PollutionDebugKind Kind { get; set; }
    [ProtoMember(6)] public float Strength { get; set; }
}

[ProtoContract]
public sealed class SleepEyeSnapshot
{
    [ProtoMember(1)] public double OpenTime { get; set; }
    [ProtoMember(2)] public double Phase { get; set; }
    [ProtoMember(3)] public double Hold { get; set; }
    [ProtoMember(4)] public bool Closing { get; set; }
    [ProtoMember(5)] public long Sequence { get; set; }
}

[ProtoContract]
public sealed class DebugValueRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = Protocol.Version;
    [ProtoMember(2)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(3)] public long RequestId { get; set; }
    [ProtoMember(4)] public long LifeGeneration { get; set; }
    [ProtoMember(5)] public string Field { get; set; } = "";
    [ProtoMember(6)] public double Value { get; set; }
}

[ProtoContract]
public sealed class DebugValueResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Accepted { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public string SessionEpoch { get; set; } = "";
}

[ProtoContract]
public sealed class FunctionSnapshot
{
    [ProtoMember(1)] public bool Movement { get; set; }
    [ProtoMember(2)] public bool Manipulation { get; set; }
    [ProtoMember(3)] public double Jump { get; set; } = 1;
    [ProtoMember(4)] public double Sprint { get; set; } = 1;
    [ProtoMember(5)] public double Bow { get; set; } = 1;
    [ProtoMember(6)] public double Mining { get; set; } = 1;
    [ProtoMember(7)] public bool CanJump { get; set; } = true;
    [ProtoMember(8)] public bool CanSprint { get; set; } = true;
    [ProtoMember(9)] public bool CanDrawBow { get; set; } = true;
    [ProtoMember(10)] public bool CanUseActiveHand { get; set; } = true;
    [ProtoMember(11)] public string ExhaustionReason { get; set; } = "";
    [ProtoMember(12)] public bool OxygenReplacement { get; set; }
    [ProtoMember(13)] public double HandCapacity { get; set; } = 1;
    [ProtoMember(14)] public double LaborLoad { get; set; } = 1;
    [ProtoMember(15)] public double SprintLoad { get; set; } = 1;
    [ProtoMember(16)] public double Fatigue { get; set; }
    [ProtoMember(17)] public double SleepPressure { get; set; }
    [ProtoMember(18)] public bool BodyCanJump { get; set; } = true;
    [ProtoMember(19)] public double PaceGain { get; set; } = 1;
    [ProtoMember(20)] public double MeleeAttackSpeed { get; set; } = 1;
}

[ProtoContract]
public sealed class PanelMetric
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public double Value { get; set; }
    [ProtoMember(3)] public double Maximum { get; set; }
    [ProtoMember(4)] public double Minimum { get; set; }
    [ProtoMember(5)] public bool Available { get; set; }
    [ProtoMember(6)] public bool Bar { get; set; }
    [ProtoMember(7)] public string Unit { get; set; } = "";
    [ProtoMember(8)] public string Text { get; set; } = "";
    [ProtoMember(9)] public string Detail { get; set; } = "";
}

[ProtoContract]
public sealed class StatusIconSnapshot
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public bool Available { get; set; }
    [ProtoMember(3)] public bool Active { get; set; }
    [ProtoMember(4)] public string Detail { get; set; } = "";
}

[ProtoContract]
public sealed class NutrientGauge
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public double Value { get; set; }
    [ProtoMember(3)] public double Maximum { get; set; }
    [ProtoMember(4)] public double Pending { get; set; }
    [ProtoMember(5)] public bool Available { get; set; }
    [ProtoMember(6)] public double Deficiency { get; set; }
    [ProtoMember(7)] public int Stage { get; set; }
}

[ProtoContract]
public sealed class WorldActionProgress
{
    [ProtoMember(1)] public string Action { get; set; } = "";
    [ProtoMember(2)] public double ElapsedSeconds { get; set; }
    [ProtoMember(3)] public double DurationSeconds { get; set; }
}

[ProtoContract]
public sealed class WaterHoverSnapshot
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Dimension { get; set; }
    [ProtoMember(5)] public string[] Lines { get; set; } = [];
    [ProtoMember(6)] public bool ClassificationPending { get; set; }
    [ProtoIgnore] public string? Formatted { get; set; }
}

[ProtoContract]
public sealed class BodyRegionSnapshot
{
    [ProtoMember(1)] public string Region { get; set; } = "";
    [ProtoMember(2)] public double Integrity { get; set; }
    [ProtoMember(3)] public double Maximum { get; set; } = 15;
    [ProtoMember(4)] public bool Wound { get; set; }
    [ProtoMember(5)] public bool Pain { get; set; }
}

[ProtoContract]
public sealed class HudGauge
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public double Value { get; set; }
    [ProtoMember(3)] public double Maximum { get; set; } = 1;
    [ProtoMember(4)] public bool Available { get; set; }
    [ProtoMember(5)] public string Text { get; set; } = "";
}

[ProtoContract]
public sealed class WorldActionRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = Protocol.Version;
    [ProtoMember(2)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(3)] public long Sequence { get; set; }
    [ProtoMember(4)] public string Action { get; set; } = "";
    [ProtoMember(5)] public long ActionId { get; set; }
}

[ProtoContract]
public sealed class PressureStatus
{
    [ProtoMember(1)] public long Sequence { get; set; }
    [ProtoMember(2)] public long ActionId { get; set; }
    [ProtoMember(3)] public bool Active { get; set; }
    [ProtoMember(4)] public string Region { get; set; } = "general";
    [ProtoMember(5)] public double ElapsedSeconds { get; set; }
    [ProtoMember(6)] public double BeforeMlPerMinute { get; set; }
    [ProtoMember(7)] public double EffectiveMlPerMinute { get; set; }
    [ProtoMember(8)] public string Reason { get; set; } = "";
}

[ProtoContract]
public sealed class SettingsRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = Protocol.Version;
    [ProtoMember(2)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(3)] public long RequestId { get; set; }
    [ProtoMember(4)] public long ExpectedRevision { get; set; }
    [ProtoMember(5)] public double PhysiologySpeed { get; set; }
    [ProtoMember(6)] public bool TelemetryEnabled { get; set; }
}

[ProtoContract]
public sealed class SettingsResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Accepted { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public string SessionEpoch { get; set; } = "";
}

[ProtoContract]
public sealed class ClinicalEffectSnapshot
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public double Severity { get; set; }
    [ProtoMember(3)] public bool Beneficial { get; set; }
    [ProtoMember(4)] public string Description { get; set; } = "";
}

[ProtoContract]
public sealed class ActivityLedger
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public string Activity { get; set; } = "Standing";
    [ProtoMember(3)] public double DemandMultiplier { get; set; } = 1;
    [ProtoMember(4)] public double PaidKcalPerGameHour { get; set; }
    [ProtoMember(5)] public double StaminaPerSecond { get; set; }
    [ProtoMember(6)] public double OxygenPerSecond { get; set; }
    [ProtoMember(7)] public double FatiguePerGameHour { get; set; }
    [ProtoMember(8)] public PressureStatus? Pressure { get; set; }
    [ProtoMember(9)] public string DeniedReason { get; set; } = "";
    [ProtoMember(10)] public ExertionState? Exertion { get; set; }
    [ProtoMember(11)] public bool StateOnly { get; set; }
}

[ProtoContract]
public sealed class BedRestSnapshot
{
    [ProtoMember(1)] public double EnvironmentEfficiency { get; set; }
    [ProtoMember(2)] public double TreatmentSupport { get; set; }
    [ProtoMember(3)] public double PressureRecoveryPerGameHour { get; set; }
    [ProtoMember(4)] public double FatigueRecoveryPerGameHour { get; set; }
}

[ProtoContract]
public sealed class ExertionState
{
    [ProtoMember(1)] public long Sequence { get; set; }
    [ProtoMember(2)] public long Acknowledged { get; set; }
    [ProtoMember(3)] public double Stamina { get; set; }
    [ProtoMember(4)] public double Lactate { get; set; }
    [ProtoMember(5)] public double Capacity { get; set; }
    [ProtoMember(6)] public double Endurance { get; set; } = 1;
    [ProtoMember(7)] public bool Exhausted { get; set; }
    [ProtoMember(8)] public double[] Credits { get; set; } = [];
    [ProtoMember(9)] public int Life { get; set; }
    [ProtoMember(10)] public FunctionSnapshot? Functions { get; set; }
    [ProtoMember(11)] public int RecoveryStage { get; set; }
    [ProtoMember(12)] public int RecoveryPolicyVersion { get; set; }
    [ProtoMember(13)] public double JumpCooldownRemainingSeconds { get; set; }
    [ProtoMember(14)] public double ExhaustedJumpCooldownSeconds { get; set; } = 3;
    [ProtoMember(15)] public double UnlockStamina { get; set; } = .20;
    [ProtoMember(16)] public double UnlockLactateRatio { get; set; } = .6;
    [ProtoMember(17)] public bool RunningHeld { get; set; }
    [ProtoMember(18)] public double RunningElapsed { get; set; }
    [ProtoMember(19)] public double RunningReleased { get; set; } = 30;
    [ProtoMember(20)] public bool RunningBurst { get; set; }
    [ProtoMember(21)] public double BurstSeconds { get; set; } = 3;
    [ProtoMember(22)] public double BurstTransitionSeconds { get; set; } = 1;
    [ProtoMember(23)] public double BurstRearmSeconds { get; set; } = 2;
    [ProtoMember(24)] public double RunningSpeedGain { get; set; } = .8;
    [ProtoMember(25)] public double RunningStaminaPerSecond { get; set; } = .009;
    // Tags 26 and 27 belonged to the removed running recovery policy. Never reuse.
    [ProtoMember(28)] public double JoggingSpeedGain { get; set; } = .25;
    [ProtoMember(29)] public double RunningSlowdownStamina { get; set; } = .20;
    [ProtoMember(30)] public double RunningLimit { get; set; } = 1;
}
[ProtoContract]
public sealed class WorkIntent
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public long Sequence { get; set; }
    [ProtoMember(3)] public bool TriggerHeld { get; set; }
    [ProtoMember(4)] public WorkNotice[] Operations { get; set; } = [];
    [ProtoMember(5)] public int Life { get; set; }
}
[ProtoContract]
public sealed class WorkNotice
{
    [ProtoMember(1)] public long Sequence { get; set; }
    [ProtoMember(2)] public int Kind { get; set; }
    [ProtoMember(3)] public double Seconds { get; set; }
}
