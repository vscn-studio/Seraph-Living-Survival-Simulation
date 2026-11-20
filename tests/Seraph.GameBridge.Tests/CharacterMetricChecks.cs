using System.Reflection;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

internal static class CharacterMetricChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // Metrics need localization, not a successful run of unrelated container interactions.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        foreach (var locale in new[] { "en", "zh-cn" })
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(root, "assets/vsslssmod/lang", locale + ".json")))!;
            var service = new Vintagestory.API.Config.TranslationService(locale, null!);
            service.GetType().GetField("entryCache", flags)!.SetValue(service,
                entries.ToDictionary(p => "vsslssmod:" + p.Key, p => p.Value));
            service.GetType().GetField("loaded", flags)!.SetValue(service, true);
            Vintagestory.API.Config.Lang.AvailableLanguages[locale] = service;
        }
        Vintagestory.API.Config.Lang.ChangeLanguage("zh-cn");
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, null!);
        player.WorldData = (IWorldPlayerData)RoutingProxy.Make(typeof(IWorldPlayerData), (m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        var world = (IServerWorldAccessor)RoutingProxy.Make(typeof(IServerWorldAccessor), (m, _) => m.Name == "get_ElapsedMilliseconds" ? 0L : throw new Exception(m.Name));
        var api = (ICoreServerAPI)RoutingProxy.Make(typeof(ICoreServerAPI), (m, _) => m.Name == "get_World" ? world : throw new Exception(m.Name));
        var type = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(type, [api])!;
        type.GetField("capabilities", flags)!.SetValue(server, CapabilityRegistry.Create(new(), []));
        var sessionType = type.GetNestedType("Session", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        void Set(string key, object value) => sessionType.GetProperty(key)!.SetValue(session, value);
        PanelMetric[] Read(string method) => (PanelMetric[])type.GetMethod(method, flags)!.Invoke(server, [session])!;
        var body = BodyState.Create();
        var frame = FrameworkState.Create(player.PlayerUID) with {
            Dirtiness = .3, AirPollution = .8, ImmuneReserve = .6,
            Blood = new() { LactateG = 12, WasteMl = 30, Toxicity = .2, RegenerationMlPerGameHour = 45, FiltrationMlPerGameHour = 22 },
            Diseases = [new() { Pathogen = "norovirus", Phase = DiseasePhase.Exposed, CumulativeHazard = .1 },
                new() { Pathogen = "giardia", Phase = DiseasePhase.Symptomatic, Burden = .4 }]
        };
        StatusIconSnapshot[] Icons() => (StatusIconSnapshot[])type.GetMethod("StatusIcons", flags)!.Invoke(server, [session])!;
        foreach (var mass in new[] { 0d, 500d, 3999d, 4000d, 5000d })
        {
            Set("Interactions", new InteractionState { BowelUnits = mass });
            var bowelIcon = Icons().Single(i => i.Key == "defecation");
            Check(bowelIcon.Active == (mass >= 4000) && bowelIcon.Detail.Length > 0,
                "Bowel warning conflated the 10% manual minimum with the 80% warning, or omitted the load detail.");
        }
        Set("Interactions", new InteractionState());
        foreach (var (reserve, temp, expected) in new[] { (8000d, 38d, false), (800d, 38d, false),
            (799d, 37.5, true), (799d, 37.499, false), (8000d, 39.5, true) })
        {
            var warm = body with { BodyWaterL = BodyProfile.Default.ReferenceBodyWaterL - (8000 - reserve) / 1000,
                CoreTemperatureC = temp };
            Set("State", warm); Set("Framework", frame);
            var symptoms = (ClinicalEffectSnapshot[])type.GetMethod("ClinicalProjection", flags)!.Invoke(server, [session])!;
            var sleepRisks = (int)type.GetMethod("SleepRisks", flags)!.Invoke(server, [session, warm, frame])!;
            Check(Icons().Single(i => i.Key == "heat-illness").Active == expected
                && symptoms.Any(e => e.Key == "heat-illness") == expected && ((sleepRisks & 2) != 0) == expected,
                "Heat icon, symptoms and sleep warnings disagree about hydration/temperature boundaries.");
        }
        Set("State", body);
        Set("Framework", frame with { Syndromes = frame.Syndromes with { VectorAttached = true } });
        var icons = Icons();
        Check(icons.Length == 16 && icons.Single(i => i.Key == "bug").Active
            && !icons.Single(i => i.Key == "dysentery").Active, "Attached insect was conflated with enteric disease.");
        Set("Framework", frame with { Diseases = [new() { Pathogen = "shigella", Phase = DiseasePhase.Symptomatic, Burden = .5 }] });
        icons = Icons();
        Check(!icons.Single(i => i.Key == "bug").Active && icons.Single(i => i.Key == "dysentery").Active,
            "Dysentery did not have an independent status icon.");
        foreach (var (reserve, known, burden, expected) in new[] {
            (50d, true, 0d, false), (100d, true, 0d, false), (.01d, true, 0d, false),
            (0d, false, 0d, false), (50d, true, .4d, true), (0d, true, .2d, true), (100d, true, .4d, true), (0d, false, 1d, false) })
        {
            var nutrition = frame.Nutrition with { Reserve = (double[])frame.Nutrition.Reserve.Clone(),
                Known = (bool[])frame.Nutrition.Known.Clone(), DeficiencyBurden = (double[])frame.Nutrition.DeficiencyBurden.Clone() };
            foreach (var id in MicroNutrition.Groups)
            {
                var i = Nutrients.IndexOf(id); nutrition.Reserve[i] = reserve;
                nutrition.Known[i] = known; nutrition.DeficiencyBurden[i] = burden;
            }
            Set("Framework", frame with { Nutrition = nutrition });
            var clinical = (ClinicalEffectSnapshot[])type.GetMethod("ClinicalProjection", flags)!.Invoke(server, [session])!;
            foreach (var (key, effect) in new[] { ("bone-weakness", "bone-weakness"), ("anemia", "anemia"),
                ("bcomplex-deficiency", "bcomplex-deficiency"), ("vitamina-deficiency", "night-vision"), ("vitaminc-deficiency", "vitaminc-deficiency") })
                Check(Icons().Single(i => i.Key == key).Active == expected
                    && clinical.Any(e => e.Key == effect) == expected,
                    $"Deficiency icon and clinical effect disagree for reserve={reserve}, known={known}, burden={burden}: {key}");
        }
        var environment = EnvironmentSample.Neutral with { AirPollution = .2, EnvironmentalContamination = .35 };
        Set("Framework", frame); Set("Environment", environment);
        var physicalBefore = (BodyState)sessionType.GetProperty("State")!.GetValue(session)!;
        var status = Read("StatusMetrics");
        Check(status.Length == 28 && !status.Any(m => m.Key is "waterdeficit" or "plasma" or "coretemp" or "skintemp" or "conditions"
                or "spo2" or "bloodpressure" or "adrenaline"),
            "Retired or relocated body fields remain in the snapshot.");
        Check(status.Single(m => m.Key == "heartrate") is { Unit: "bpm" } pulse
            && pulse.Value == AdrenalineEffects.HeartRate(frame)
            && status.Single(m => m.Key == "breathrate") is { Unit: "min⁻¹" } breathing
            && breathing.Value == frame.BreathDemandPerMinute,
            "Circulation panel differs from the effective heart or breathing rate.");
        Check(status.Where(m => m.Key.StartsWith("energy", StringComparison.Ordinal)).All(m => !m.Available),
            "Energy demand appeared before any accepted physiology step.");
        Set("EnergyBasalKcalPerGameHour", 80d); Set("EnergyColdKcalPerGameHour", 15d);
        Set("EnergyActivityKcalPerGameHour", 120d); Set("EnergyRecoveryKcalPerGameHour", 4d);
        Set("EnergyTotalKcalPerGameHour", 219d); Set("HasEnergySnapshot", true);
        status = Read("StatusMetrics");
        Check(status.Where(m => m.Key.StartsWith("energy", StringComparison.Ordinal)).All(m => m.Available && m.Unit == "kcal/h")
            && status.Single(m => m.Key == "energytotal").Value == 219,
            "Accepted physiology demand was not projected from the server's energy ledger.");
        Check(status.Single(m => m.Key == "health").Maximum == 100, "Health display does not use the new 100-point account.");
        Check(status.Single(m => m.Key == "lactate").Value == 12
            && status.Single(m => m.Key == "bloodwaste").Value == 30
            && status.Single(m => m.Key == "bloodtoxicity").Value == 20 && status.Single(m => m.Key == "bloodregen").Value == 45
            && status.Single(m => m.Key == "bloodclearance").Value == 22,
            "Circulation metrics differ from the committed server state.");
        Check(status.Where(m => m.Bar && m.Key is not ("bodywater" or "blood" or "lactate" or "bloodwaste" or "bowel" or "musclemass" or "heartrate" or "spo2" or "bloodpressure" or "bloodglucose" or "whitecells" or "ketosis")).All(m => m.Maximum == 100 && m.Unit == ""),
            "Body reserve scores retain inconsistent maxima or physical units.");
        foreach (var (key, unit, maximum) in new[] { ("blood", "L", BodyProfile.Default.ReferenceBloodVolumeL),
            ("lactate", "g", BloodCirculation.LactateCapacityG(body, BodyProfile.Default)),
            ("bloodwaste", "mL", BloodCirculation.WasteCapacityMl(body, BodyProfile.Default)),
            ("bowel", "g", 5000d), ("musclemass", "kg", BodyProfile.Default.MuscleMassMaxG / 1000) })
            Check(status.Single(m => m.Key == key) is var physical && physical.Unit == unit && physical.Maximum == maximum,
                "Physical reserve lost its unit or capacity: " + key);
        Check(status.Single(m => m.Key == "bodywater") is { Value: 8000, Maximum: 8000, Unit: "g" },
            "Hydration still displays total body water instead of available reserve.");
        foreach (var ratio in new[] { .4, .55, .7, .85, 1.2 })
        {
            var loaded = BloodCirculation.AddWaste(FrameworkState.Create(player.PlayerUID), body, BodyProfile.Default,
                BloodCirculation.WasteCapacityMl(body, BodyProfile.Default) * ratio);
            Set("Framework", loaded);
            var metrics = Read("StatusMetrics");
            Check(Math.Abs(metrics.Single(m => m.Key == "bloodtoxicity").Value - Math.Clamp((ratio - .4) / .45, 0, 1) * 100) < 1e-9
                && metrics.Single(m => m.Key == "bloodtoxicity").Detail.Length > 0
                && metrics.Single(m => m.Key == "bloodwaste").Detail.Contains(metrics.Single(m => m.Key == "bloodtoxicity").Detail)
                && ReferenceEquals(loaded, sessionType.GetProperty("Framework")!.GetValue(session)),
                "Raw waste detail changed functional toxicity or physical state.");
        }
        Set("Framework", frame);
        Check(ReferenceEquals(frame, sessionType.GetProperty("Framework")!.GetValue(session))
            && ReferenceEquals(physicalBefore, sessionType.GetProperty("State")!.GetValue(session)),
            "Display normalization changed physical accounts.");
        var thermal = Read("TemperatureMetrics");
        Check(thermal.Single(m => m.Key == "airpollution").Value == 20, "Environment displays the player's old exposure instead of ambient pollution.");
        Check(!thermal.Single(m => m.Key == "heatproduction").Available && !thermal.Single(m => m.Key == "sweat").Available,
            "No accepted thermal step fabricated an actual rate.");
        Set("Thermal", new ThermalLedger(2, 200, 180, 60, .001));
        thermal = Read("TemperatureMetrics");
        Check(Math.Abs(thermal.Single(m => m.Key == "heatproduction").Value - 100 * 3600d / 4184) < 1e-9
            && Math.Abs(thermal.Single(m => m.Key == "heatbalance").Value - 10 * 3600d / 4184) < 1e-9
            && thermal.Single(m => m.Key == "sweat").Value == 1.8, "Thermal rates lost accepted time or signed heat balance.");
        Check(thermal.Where(m => m.Key is "heatproduction" or "heatloss" or "heatbalance" or "shivering")
            .All(m => m.Unit == "kcal/h"), "Thermal flows do not use the common h energy unit.");
        var inline = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!.GetMethod("InlineMetricText", BindingFlags.Static | BindingFlags.NonPublic)!;
        var line = (string)inline.Invoke(null, ["heatproduction", thermal.Single(m => m.Key == "heatproduction")])!;
        Check(line.Contains("86") && !line.Contains('\n') && line.Contains("kcal/h"), "Thermal row omitted its game-hour unit or split its value.");
        var recovery = Read("RecoveryMetrics");
        Check(recovery.Take(4).Select(m => m.Key).SequenceEqual(new[] { "dirtiness", "immune", "immunesystem", "healingrate" })
            && recovery.All(m => !m.Bar) && recovery.Length == 6, "Immune data still includes bars or retired placeholders.");
        Check(recovery.Skip(4).Select(m => m.Key).SequenceEqual(new[] { "disease-norovirus", "disease-giardia" }),
            "Exposed and infected diseases are not separate rows.");
        Check(Math.Abs(recovery.Single(m => m.Key == "immunesystem").Value
            - ImmuneSystem.Effective(frame) * 100) < 1e-9,
            "Immunity display differs from actual effective immunity.");
        var functions = Read("FunctionMetrics");
        Set("State", body with { SleepPressure = 1 });
        var sleepEyes = (SleepEyeCycle)sessionType.GetProperty("SleepEyes")!.GetValue(session)!;
        sleepEyes.Restore(new(0, .8, .7, true, 1));
        Check(Read("FunctionMetrics").Where(m => m.Key is "dodge" or "dodgefront" or "dodgeback").All(m => m.Value == 0),
            "Closed-eye dodge metrics retained directional bonuses.");
        sleepEyes.Reset(); Set("State", body);
        Check(functions.Length == 15 && functions.Select(m => m.Key).Take(6).SequenceEqual(
            new[] { "consciousness", "mobility", "respiratory", "circulation", "digestion", "immunity" })
            && !functions.Any(m => m.Key == "adrenaline"),
            "Core functions, combat defenses or healing rate are missing.");
        Check(functions.All(m => !m.Bar && m.Detail.Length > 0)
            && functions.Where(m => m.Key != "healingrate").All(m => m.Text.EndsWith('%') && !m.Text.Contains('.'))
            && functions.Single(m => m.Key == "healingrate").Text == recovery.Single(m => m.Key == "healingrate").Text,
            "Functions require integer text and primary reasons.");
        Check(functions.Single(m => m.Key == "immunity").Value == recovery.Single(m => m.Key == "immunesystem").Value,
            "Two panels disagree about effective immunity.");
        foreach (var candidate in new[] { frame with { Stamina = .01, Fatigue = 1 },
            frame with { AdrenalineLevel = 1, AdrenalineRemainingSeconds = 20 },
            frame with { Vitals = frame.Vitals.Change(-3, 4) },
            frame with { Vitals = frame.Vitals.Change(-15, 4) },
            frame with { Stamina = 0, ExertionExhausted = true },
            frame with { Stamina = .4, ExertionExhausted = true, Blood = new() { LactateG = 80 } } })
        {
            Set("Framework", candidate); Set("State", body with { SleepPressure = 1 });
            var resolved = BodyFunctionResolver.Evaluate(candidate, body with { SleepPressure = 1 }, BodyProfile.Default);
            var expected = !resolved.CanJump ? 0
                : resolved.Jump >= .9 - 1e-9 ? 1 : Math.Sqrt(Math.Clamp(resolved.Jump, 0, 1));
            var jump = Read("FunctionMetrics").Single(m => m.Key == "jump");
            Check(Math.Abs(jump.Value - expected * 100) < 1e-9 && jump.Detail.Length > 0,
                "C-page jump differs from native takeoff or reports an unapplied bonus.");
            if (candidate.ExertionExhausted) Check(jump.Detail.Contains("20%") && !jump.Detail.Contains("60%"),
                "Exhausted jump reports a stale lactate unlock condition.");
        }
        Set("Framework", frame); Set("State", body);
        var before = (FrameworkState)sessionType.GetProperty("Framework")!.GetValue(session)!;
        Read("RecoveryMetrics"); Read("TemperatureMetrics"); Read("FunctionMetrics");
        Check(ReferenceEquals(before, sessionType.GetProperty("Framework")!.GetValue(session)), "Reading character windows mutated physiology.");
        var illness = FrameworkState.Create("clinical-integration") with { Diseases = [
            new() { Pathogen = "shigella", Phase = DiseasePhase.Symptomatic, Burden = .7, InfectionThreshold = 1 },
            new() { Pathogen = "qfever", Phase = DiseasePhase.Symptomatic, Burden = .6, InfectionThreshold = 1 }] };
        type.GetField("settings", flags)!.SetValue(server, new SettingsAuthority(new() { Circulation = new() { FiltrationMlPerGameHour = 0 } }));
        var prepare = type.GetMethod("PrepareStepInputs", flags)!;
        type.GetField("settings", flags)!.SetValue(server, new SettingsAuthority(new()));
        var depleted = body with { BodyWaterL = BodyProfile.Default.ReferenceBodyWaterL - 8, GutWaterL = 3.836 };
        var healthyGut = FrameworkState.Create("hydration-integration");
        foreach (var sleeping in new[] { false, true })
        {
            var drinkingInputs = (StepInputs)prepare.Invoke(server, [session, healthyGut, depleted, sleeping, .8d, 0d, 8d])!;
            Check(drinkingInputs.DigestionEfficiency == .15 && drinkingInputs.WaterAbsorptionEfficiency == 1,
                "Server applied food digestion/dehydration to water absorption.");
            var rehydrated = new SimulationKernel(BodyProfile.Default).Advance(depleted, environment, drinkingInputs, 8);
            Check(rehydrated.Accepted && rehydrated.Diagnostics.AbsorbedWaterL > .003,
                "Server inputs cannot restore water during severe dehydration or sleep.");
        }
        var renalInputs = (StepInputs)prepare.Invoke(server, [session, illness with { Blood = new() { WasteMl = 1200 } }, body, true, .8d, 0d, 8d])!;
        Check(renalInputs.RenalSupport == .5 && Math.Abs(renalInputs.BasalMetabolismMultiplier - .75 * 1.275) < 1e-12,
            "Renal filtration is not connected to the server's actual metabolic step.");
        type.GetField("settings", flags)!.SetValue(server, new SettingsAuthority(new() { Circulation = new() { FiltrationMlPerGameHour = 0 } }));
        var inputs = (StepInputs)prepare.Invoke(server, [session, illness, body, true, .8d, 0d, 8d])!;
        Check(Math.Abs(inputs.EntericWaterLossLps - .5 / 3600) < 1e-12 && inputs.FeverTemperatureRiseC > 0,
            "Server step omitted the acute dysentery flow or fever thermal target.");
        var step = new SimulationKernel(BodyProfile.Default).Advance(body, environment, inputs, 8);
        Check(step.Accepted, step.Error ?? "Clinical integration failed.");
        var settle = type.GetMethod("SettleClinicalStep", flags)!;
        var result = (WoundRecoveryResult)settle.Invoke(server, [session, illness, illness, step.State, inputs, 8d, body, step.Diagnostics, 400d])!;
        Check(result.Body.BodyWaterL == step.State.BodyWaterL
            && Math.Abs(result.BowelG + result.State.Syndromes.BowelContentsG - step.Diagnostics.EntericWaterLossL * 1000) < 1e-8,
            "Server syndrome integration charged diarrhea twice or lost its bowel mass.");
        var waterBefore = frame with { Blood = new(), Diseases = [],
            Nutrition = frame.Nutrition with { AbsorbedWaterWasteMl = 100, PendingWaterWasteMl = 50 } };
        var waterAfter = waterBefore with { Nutrition = waterBefore.Nutrition with { AbsorbedWaterWasteMl = 112, PendingWaterWasteMl = 38 } };
        var absorbed = (WoundRecoveryResult)settle.Invoke(server,
            [session, waterBefore, waterAfter, body, new StepInputs(), 8d, body, new SimulationDiagnostics(), 400d])!;
        Check(Math.Abs(absorbed.State.Blood.WasteMl - 12) < 1e-9 && absorbed.BowelG == 0,
            "Water impurity was charged from the cumulative ledger or directly duplicated in the bowel.");
        // Accepted metabolic and recovery fuel use the same settlement for awake and sleeping steps.
        var lowFuel = body with { GlycogenEnergyKJ = 0 };
        var ketoFrame = FrameworkState.Create("ketosis-integration");
        WoundRecoveryResult SettleFuel(FrameworkState old, FrameworkState next, bool sleeping, SimulationDiagnostics fuel) =>
            (WoundRecoveryResult)settle.Invoke(server, [session, old, next, lowFuel,
                new StepInputs { IsSleeping = sleeping }, 8d, lowFuel, fuel, 400d])!;
        foreach (var sleeping in new[] { false, true })
        {
            var fatStep = SettleFuel(ketoFrame, ketoFrame, sleeping, new() { FatConsumedKJ = 8 });
            var expected = Ketosis.Advance(0, fatStep.Body.GlycogenEnergyKJ / BodyProfile.Default.GlycogenCapacityKJ,
                0, 8 + fatStep.EnergyKJ, 0, 8);
            Check(Math.Abs(fatStep.State.Clinical.Ketosis - expected) < 1e-12 && expected > 0,
                "Accepted fat flow did not drive ketosis during awake/sleep settlement.");
            var healed = ketoFrame with { Vitals = ketoFrame.Vitals.Change(-1),
                Nutrition = ketoFrame.Nutrition with { Reserve = ketoFrame.Nutrition.Reserve.Select((v, i) => i == 1 ? 2 : v).ToArray(), RepairEnergyKJ = 2 * DietaryMetabolism.ProteinKJPerG } };
            var recoveryOnly = SettleFuel(healed, healed, sleeping, new());
            Check(recoveryOnly.EnergyKJ > 0 && recoveryOnly.State.Clinical.Ketosis > 0,
                "Accepted recovery fat fuel was omitted from ketosis.");
            var noFlow = ketoFrame with { Clinical = ketoFrame.Clinical with { Ketosis = .6 } };
            var decay = SettleFuel(noFlow, noFlow, sleeping, new());
            Check(Math.Abs(decay.State.Clinical.Ketosis - Ketosis.Advance(.6, 0, 0, 0, 0, 8)) < 1e-12,
                "No-flow settlement did not decay ketosis or mutated its input.");
        }
        Set("Framework", ketoFrame with { Clinical = ketoFrame.Clinical with { Ketosis = .6 } });
        Check(Icons().Single(i => i.Key == "ketosis").Active
            && Read("StatusMetrics").Single(m => m.Key == "ketosis").Value == 60,
            "Ketosis icon and C metric disagree with accepted state.");
        type.GetField("settings", flags)!.SetValue(server, new SettingsAuthority(new() { EnableClinicalEffects = false }));
        var frozen = illness with { Clinical = illness.Clinical with { Ketosis = .6 }, Syndromes = new() { BowelContentsG = 100, VectorAttached = true } };
        inputs = (StepInputs)prepare.Invoke(server, [session, frozen, body, true, .8d, 0d, 8d])!;
        result = (WoundRecoveryResult)settle.Invoke(server, [session, frozen, frozen, body, inputs, 8d, body, new SimulationDiagnostics(), 400d])!;
        Check(inputs.EntericWaterLossLps == 0 && inputs.FeverTemperatureRiseC == 0 && inputs.BasalMetabolismMultiplier == .75
            && result.State.Syndromes == frozen.Syndromes && result.BowelG == 0 && result.State.Clinical.Ketosis == .6,
            "Disabling clinical effects continued symptom water loss or syndrome transit.");
        Console.WriteLine("PASS authoritative character metrics: 100 health, one water account, ambient environment, accepted thermal rates, text-only immunity, per-disease rows and read-only refresh");
    }

    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
