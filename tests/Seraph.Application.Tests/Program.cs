using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Seraph.Application;
using Seraph.Core.State;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;

var failed = 0;
var count = 0;
void Check(string name, Action action)
{
    count++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
Check("Release v1 identity, rejection and byte preservation", ReleaseFormatChecks.Run);
Check("Metric trends track actual five-second deltas, range changes, stale samples and active time", () =>
{
    var history = new MetricTrends();
    history.Record("water", 50, 0, 100); history.Advance(.2); history.Record("water", 45, 0, 100);
    Require(history.Get("water") == (.5, .45, -1));
    for (var n = 0; n < 26; n++) { history.Advance(.2); history.Record("water", 45, 0, 100); }
    Require(history.Get("water").Direction == 0);
    history.Record("water", 60, 0, 100); Require(history.Get("water").Direction == 1);
    history.Record("water", 60, 0, 200); Require(history.Get("water").Direction == 0);
    history.Record("hud-stamina", .5, 0, 1); history.Advance(.2); history.Record("hud-stamina", .49, 0, 1);
    Require(history.Get("hud-stamina").Direction == -1);
    var paused = history.Get("water"); Require(history.Get("water") == paused);
    history.Advance(.25); history.Advance(.25); history.Advance(.25); Require(history.Get("water") == default);
    history.Record("water", double.NaN, 0, 100); Require(history.Get("water") == default);
    history.Clear(); Require(history.Time == 0 && history.Get("hud-stamina") == default);
});

var save = new ShadowSave { WorldId = "world-1", PlayerUid = "player-1", Profile = BodyProfile.Default, State = BodyState.Create(), CheckpointSequence = 23 };

Check("Seven-hour bed sleep supports a daily cycle without speeding seated rest or custom clocks", () =>
{
    var config = new SeraphServerConfig().ResolveUnits();
    var profile = BodyProfile.Default with { BasalMetabolicPowerW = 0 };
    var kernel = new SimulationKernel(profile, new() { EnableDryHeatExchange = false, EnableRespiration = false,
        EnableEvaporation = false, EnableSweating = false, EnableInsensibleWaterLoss = false,
        EnableFecalWaterLoss = false, MaximumShiveringPowerW = 0 });
    var body = BodyState.Create(profile) with { SleepPressure = .8 };
    var sleep = new StepInputs { IsSleeping = true, SleepQuality = .45,
        SleepPressureRecoverySeconds = config.SleepPressureRecoverySeconds / config.BedSleepPressureRecoveryMultiplier };
    void Advance(double hours, StepInputs inputs) {
        for (var i = 0; i < hours * 3600 / 8; i++) {
            var step = kernel.Advance(body, EnvironmentSample.Neutral, inputs, 8);
            Require(step.Accepted); body = step.State;
        }
    }
    Advance(7, sleep);
    Require(Math.Abs(body.SleepPressure - .0125) < 1e-9);
    for (var day = 0; day < 7; day++) {
        Advance(17, new() { SleepPressureRiseSeconds = config.SleepPressureRiseSeconds });
        Require(body.SleepPressure is >= .68 and < .71); // First penalties start at 70%.
        Advance(7, sleep);
        Require(body.SleepPressure < .02);
    }
    var seated = body with { SleepPressure = .8 };
    var result = kernel.Advance(seated, EnvironmentSample.Neutral,
        new() { IsResting = true, SleepPressureRecoverySeconds = config.SleepPressureRecoverySeconds }, 8);
    Require(Math.Abs(result.State.SleepPressure - (.8 - .385 * 8 / 28800)) < 1e-10);
    var custom = new SeraphServerConfig { BedSleepPressureRecoveryMultiplier = 1, SleepPressureRecoveryGameHours = 12 }.ResolveUnits();
    Require(custom.BedSleepPressureRecoveryMultiplier == 1 && custom.SleepPressureRecoverySeconds == 43200
        && custom.ResolveUnits() == custom);
    foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, .5, 9 })
        Require((config with { BedSleepPressureRecoveryMultiplier = invalid }).Validate() is not null);
});

Check("Clinical warning anchors do not alter toxicity or diagnose incubating disease", ClinicalWarningChecks.Stages);
Check("Clinical warnings deduplicate episodes, worsen, rearm and establish silent reconnect baselines", ClinicalWarningChecks.Episodes);
Check("Clinical warnings batch bites, prioritize danger, freeze clocks and discard stale queues", ClinicalWarningChecks.Batches);



Check("Null, nonfinite and out-of-range Downed projections are rejected", () =>
{
    foreach (var downed in new DownedState?[] { null, new() { ProjectedHealth = 0 },
        new() { ProjectedHealth = 131 }, new() { ProjectedHealth = double.NaN },
        new() { ProjectedHealth = double.PositiveInfinity } })
        Require((FrameworkState.Create("invalid-downed") with { Downed = downed! }).Validate() is not null);
    var node = JsonSerializer.SerializeToNode(save with { Framework = FrameworkState.Create("null-downed") })!;
    node["Framework"]!["Downed"] = null;
    var payload = JsonSerializer.SerializeToUtf8Bytes(node);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Magic = "SERAPH", SchemaVersion = 6, Payload = payload,
        Sha256 = Convert.ToHexString(SHA256.HashData(payload)) });
    Require(!ShadowSaveCodec.Decode(bytes, save.WorldId, save.PlayerUid).Success);
});

Check("Save roundtrip preserves state and identity", () =>
{
    var decoded = ShadowSaveCodec.Decode(ShadowSaveCodec.Encode(save), "world-1", "player-1");
    Require(decoded.Success && decoded.Save!.State == save.State && decoded.Save.CheckpointSequence == 23);
});
Check("Legacy electrolyte and vitamin queues migrate to the calibrated scale", () =>
{
    var reserve = (double[])NutritionState.Starting().Reserve.Clone();
    var pending = (double[])NutritionState.Starting().Pending.Clone();
    var electrolyte = Nutrients.IndexOf("electrolytes");
    var vitaminA = Nutrients.IndexOf("a");
    var vitaminC = Nutrients.IndexOf("c");
    var vitaminB = Nutrients.IndexOf("bcomplex");
    reserve[electrolyte] = 100;
    pending[electrolyte] = 100;
    pending[vitaminA] = 22;
    pending[vitaminC] = 22;
    pending[vitaminB] = 22;
    var legacy = NutritionState.Starting() with { CalibrationVersion = 1, Reserve = reserve, Pending = pending };
    var oldSave = save with { Framework = FrameworkState.Create(save.PlayerUid) with { Nutrition = legacy } };
    var decoded = ShadowSaveCodec.Decode(ShadowSaveCodec.Encode(oldSave), save.WorldId, save.PlayerUid);
    Require(decoded.Success);
    var migrated = decoded.Save!.Framework.Nutrition;
    Require(migrated.CalibrationVersion == 2
        && Math.Abs(migrated.Get("electrolytes").Reserve - 10.5) < 1e-9
        && Math.Abs(migrated.Get("electrolytes").Pending - 10.5) < 1e-9);
    Require(Math.Abs(migrated.Get("a").Pending - MicroNutrition.DailyPointsFor("a")) < 1e-9
        && Math.Abs(migrated.Get("c").Pending - MicroNutrition.DailyPointsFor("c")) < 1e-9
        && Math.Abs(migrated.Get("bcomplex").Pending - MicroNutrition.DailyPointsFor("bcomplex")) < 1e-9);
});
Check("Save rejects wrong world or UID", () =>
{
    var bytes = ShadowSaveCodec.Encode(save);
    Require(!ShadowSaveCodec.Decode(bytes, "world-2", "player-1").Success);
    Require(!ShadowSaveCodec.Decode(bytes, "world-1", "player-2").Success);
});
Check("Corruption and oversized bytes are rejected", () =>
{
    var bytes = ShadowSaveCodec.Encode(save);
    using var doc = JsonDocument.Parse(bytes);
    var root = doc.RootElement;
    var payload = root.GetProperty("Payload").GetBytesFromBase64();
    payload[0] ^= 1;
    var corrupted = JsonSerializer.SerializeToUtf8Bytes(new { Magic = "SERAPH", SchemaVersion = 1, Payload = payload, Sha256 = root.GetProperty("Sha256").GetString() });
    Require(!ShadowSaveCodec.Decode(corrupted, "world-1", "player-1").Success);
    Require(!ShadowSaveCodec.Decode(new byte[ShadowSaveCodec.MaxBytes + 1], "world-1", "player-1").Success);
});
Check("Future envelope and truncated JSON are rejected", () =>
{
    Require(!ShadowSaveCodec.Decode("{\"Magic\":\"SERAPH\",\"SchemaVersion\":99}"u8.ToArray(), "world-1", "player-1").Success);
    Require(!ShadowSaveCodec.Decode("{"u8.ToArray(), "world-1", "player-1").Success);
});
Check("Illegal fluid state cannot be saved", () =>
{
    try { ShadowSaveCodec.Encode(save with { SchemaVersion = 2, ModelVersion = 2, State = save.State with { BodyWaterL = 0, PlasmaWaterL = 2 } }); }
    catch (InvalidDataException) { return; }
    throw new Exception("Illegal save accepted.");
});
Check("Explicit null vitals are rejected without resetting player health", () =>
{
    var node = JsonSerializer.SerializeToNode(save with { SchemaVersion = 3, ModelVersion = 3, Framework = FrameworkState.Create("player-1") })!;
    node["Framework"]!["Vitals"] = null;
    var payload = JsonSerializer.SerializeToUtf8Bytes(node);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Magic = "SERAPH", SchemaVersion = 3, Payload = payload,
        Sha256 = Convert.ToHexString(SHA256.HashData(payload)) });
    Require(!ShadowSaveCodec.Decode(bytes, "world-1", "player-1").Success);
});
Check("Version boundary supports only 1.22.x", () =>
{
    Require(TargetVersion.IsSupported("1.22.0") && TargetVersion.IsSupported("1.22.7") && TargetVersion.IsSupported("1.22.8-rc.1"));
    Require(!TargetVersion.IsSupported("1.23.0") && !TargetVersion.IsSupported("1.21.7") && !TargetVersion.IsSupported(null));
});
Check("Settings reject unauthorized, stale and nonfinite requests", () =>
{
    var authority = new SettingsAuthority(new());
    Require(authority.ValidateChange(false, 1, 2) is not null);
    Require(authority.ValidateChange(true, 99, 2) is not null);
    Require(authority.ValidateChange(true, 1, double.NaN) is not null);
    Require(authority.ValidateChange(true, 1, 20.1) is not null);
    Require(authority.Config.PhysiologySpeed == 1 && authority.Revision == 1);
    Require(authority.ValidateChange(true, 1, 0.09) is not null);
    Require(authority.ValidateChange(true, 1, 20) is null);
    authority.Commit(20, true);
    Require(authority.Revision == 2 && authority.Config.PhysiologySpeed == 20 && authority.Config.TelemetryEnabled);
});
Check("Invalid config and unsupported own mode are rejected", () =>
{
    Require((new SeraphServerConfig { Mode = "own" }).Validate() is not null);
    Require((new SeraphServerConfig { PhysiologySpeed = double.PositiveInfinity }).Validate() is not null);
    Require((new SeraphServerConfig { ConfigVersion = 999 }).Validate() is not null);
});
Check("Network burst is bounded and cleanup allows new connection", () =>
{
    var gate = new InboundGate();
    for (var i = 0; i < 10; i++) Require(gate.TryAccept("p"));
    Require(!gate.TryAccept("p"));
    Require(gate.TryAccept("other"));
    gate.Remove("p");
    Require(gate.TryAccept("p"));
});
Check("Nutrition and energy compartments survive save without storing derived buffs", () =>
{
    var frame = new FrameworkState { OxygenKnown = true, Nutrition = new() { IndependentZinc = true, CalibrationVersion = 2,
        GutMassG = 200, GutFoodWaterG = 150,
        Reserve = Nutrients.All.Select(n => n.DailyBudget * 2).ToArray(),
        Pending = Nutrients.All.Select(n => n.DailyBudget * .2).ToArray(),
        Known = Enumerable.Repeat(true, Nutrients.All.Length).ToArray(),
        DeficiencyBurden = Enumerable.Repeat(.1, Nutrients.All.Length).ToArray() } };
    var original = save with { SchemaVersion = 1, ModelVersion = 1, Framework = frame, State = save.State with { GlycogenEnergyKJ = 3500, FatEnergyKJ = 240000, GutEnergyKJ = 2000,
        MuscleTrainingStimulus = .4 } };
    var bytes = ShadowSaveCodec.Encode(original);
    var result = ShadowSaveCodec.Decode(bytes, save.WorldId, save.PlayerUid);
    Require(result.Success && result.Save!.Framework!.OxygenKnown);
    var restored = result.Save!.Framework!;
    Require(restored.Nutrition.Reserve.SequenceEqual(frame.Nutrition.Reserve)
        && restored.Nutrition.Pending.SequenceEqual(frame.Nutrition.Pending)
        && restored.Nutrition.Known.SequenceEqual(frame.Nutrition.Known)
        && restored.Nutrition.DeficiencyBurden.SequenceEqual(frame.Nutrition.DeficiencyBurden)
        && restored.Nutrition.GutMassG == 200 && restored.Nutrition.GutFoodWaterG == 150);
    Require(result.Save.State == original.State);
    Require(PlayerCondition.Evaluate(frame, original.State, original.Profile)
        == PlayerCondition.Evaluate(restored, result.Save.State, result.Save.Profile));
    using var envelope = JsonDocument.Parse(bytes);
    var payload = JsonNode.Parse(envelope.RootElement.GetProperty("Payload").GetBytesFromBase64())!;
    Require(payload["Framework"]!["RecoveryMultiplier"] is null && payload["Framework"]!["PlayerCondition"] is null);
});
Check("Malformed parallel nutrient arrays cannot be saved", () =>
{
    try { ShadowSaveCodec.Encode(save with { Framework = new() { Nutrition = new() { Reserve = [1] } } }); }
    catch (InvalidDataException) { return; }
    throw new Exception("Malformed nutrient array was accepted.");
});
Check("Injury evidence, tissues and replay receipts survive save roundtrip", () =>
{
    var result = new FrameworkKernel().RecordInjury(new(), new("hit-1", WoundKind.Fracture, "left-hand", .8,
        InjuryEvidence.Verified, 123, TissueDamage: .9, PainIntensity: .3));
    Require(result.Accepted);
    var decoded = ShadowSaveCodec.Decode(ShadowSaveCodec.Encode(save with { Framework = result.State }), save.WorldId, save.PlayerUid);
    Require(decoded.Success && JsonSerializer.Serialize(decoded.Save!.Framework!.Wounds.Single())
        == JsonSerializer.Serialize(result.State.Wounds.Single()));
    var restored = decoded.Save!.Framework!;
    Require(restored.RecentInjuryEvents.SequenceEqual(result.State.RecentInjuryEvents));
    Require(!new FrameworkKernel().RecordInjury(restored, new("hit-1", WoundKind.Cut, "general", .1, InjuryEvidence.Unlocated, 124)).Accepted);
});
Check("Action ownership respects modes, switches and external movement/combat owners", () =>
{
    foreach (var mode in new[] { "survival", "shadow", "observe" })
    {
        var config = new SeraphServerConfig { Mode = mode };
        var own = CapabilityRegistry.Create(config, []);
        var barrier = new SideEffectBarrier(mode, own);
        Require(barrier.Allows(SideEffectKind.Action, Capability.Manipulation) == (mode == "survival"));
        Require(!barrier.Allows(SideEffectKind.Action, Capability.Health));
        Require(!barrier.Allows(SideEffectKind.Inventory, Capability.Manipulation));
        var external = CapabilityRegistry.Create(config, [("carryon", "Carry On"), ("combatoverhaul", "Combat Overhaul")]);
        Require(new SideEffectBarrier(mode, external).Allows(SideEffectKind.Action, Capability.Movement) == (mode == "survival"));
        Require(!new SideEffectBarrier(mode, external).Allows(SideEffectKind.Action, Capability.Manipulation));
        var disabled = CapabilityRegistry.Create(config with { EnableMovementEffects = false, EnableInjuryActionEffects = false }, []);
        Require(!disabled.MayWrite(Capability.Movement) && !disabled.MayWrite(Capability.Manipulation));
    }
});
Check("Clinical wound care and pathogen phases survive restart", () =>
{
    var frame = new FrameworkState { EventSequence = 1, Dirtiness = .7,
        Wounds = [new() { EventSequence = 1, Kind = WoundKind.Cut, Severity = .5, BleedLps = .001, BandageQuality = 1, Contamination = .3 }],
        Diseases = [new() { Pathogen = "giardia", Phase = DiseasePhase.Incubating, InfectionThreshold = 1, PhaseSeconds = 300 }] };
    var restored = ShadowSaveCodec.Decode(ShadowSaveCodec.Encode(save with { Framework = frame }), save.WorldId, save.PlayerUid);
    Require(restored.Success && restored.Save!.Framework!.Dirtiness == .7);
    Require(JsonSerializer.Serialize(restored.Save!.Framework!.Wounds) == JsonSerializer.Serialize(frame.Wounds)
        && restored.Save.Framework.Diseases.SequenceEqual(frame.Diseases));
});
Check("Clinical config rejects nonfinite rates and animal mods do not own player bleeding", () =>
{
    Require((new SeraphServerConfig { WoundInfectionHazardPerSecond = double.NaN }).Validate() is not null);
    Require((new SeraphServerConfig { UrineProductionMlPerGameHour = double.NaN }).Validate() is null);
    Require((new SeraphServerConfig { UrineProductionMlPerGameHour = 1001 }).Validate() is null);
    Require((new SeraphServerConfig { RespiratoryTransmissionUnitsPerSecond = double.NaN }).Validate() is not null);
    Require((new SeraphServerConfig { RespiratoryProtectionByCode = new() { ["game:mask"] = 1.1 } }).Validate() is not null);
    Require((new SeraphServerConfig { RespiratorySourcesByBlockCode = new() { ["game:spore-vent"] = .25 } }).Validate() is null);
    Require((new SeraphServerConfig { RespiratorySourcesByBlockCode = new() { ["spore-vent"] = .25 } }).Validate() is not null);
    Require((new SeraphServerConfig { RespiratorySourcesByBlockCode = new() { ["game:spore-vent"] = 0 } }).Validate() is not null);
    Require((new SeraphServerConfig { RespiratorySourcesByBlockCode = new() { ["game:spore-vent"] = double.NaN } }).Validate() is not null);
    Require((new SeraphServerConfig { EntericWaterLossLPerHour = -1 }).Validate() is not null);
    Require((new SeraphServerConfig { ClinicalDamageMultiplier = double.PositiveInfinity }).Validate() is not null);
    var own = CapabilityRegistry.Create(new(), [("progressivebleeding", "Progressive Bleeding"), ("bloodtrail", "BloodTrail")]);
    Require(own.MayWrite(Capability.BleedingDamage) && own.MayWrite(Capability.ThermalDamage));
    var shadow = CapabilityRegistry.Create(new() { Mode = "shadow" }, []);
    Require(!shadow.MayWrite(Capability.BleedingDamage) && !shadow.MayWrite(Capability.ThermalDamage));
    Require(!new SideEffectBarrier("shadow", shadow).Allows(SideEffectKind.Stats, Capability.Movement));
});
Check("Sleep blur and temporary intake relief survive restart", () =>
{
    var frame = new FrameworkState { SleepBlurSeverity = .65, AlertnessReliefUntilBioSeconds = 104 };
    var restored = ShadowSaveCodec.Decode(ShadowSaveCodec.Encode(save with { Framework = frame }), save.WorldId, save.PlayerUid);
    Require(restored.Success && restored.Save!.Framework!.SleepBlurSeverity == .65
        && restored.Save.Framework.AlertnessReliefUntilBioSeconds == 104);
    Require(JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(FrameworkState.Create("release-defaults"))) is { SleepBlurSeverity: 0, AlertnessReliefUntilBioSeconds: 0 });
});
Check("Completed bowel portions clear residue without bypassing capacity or pending digestion", () =>
{
    foreach (var (load, paid, expected) in new[] { (100d,100d,0d), (199d,100d,99d), (200d,100d,100d),
        (299d,100d,199d), (299d,200d,99d), (400d,300d,100d) })
    {
        var before = new InteractionState { BowelUnits = load, PendingBowelUnits = 50 };
        var after = before with { BowelUnits = before.AfterDefecation(paid) };
        Require(after.BowelUnits == expected && after.PendingBowelUnits == 50 && before.BowelUnits == load);
    }
});
Console.WriteLine($"Application checks: {count - failed}/{count} passed.");
return failed == 0 ? 0 : 1;
