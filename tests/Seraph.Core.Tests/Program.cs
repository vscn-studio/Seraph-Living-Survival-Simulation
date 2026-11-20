using Seraph.Core.Simulation;
using Seraph.Core.State;
using Seraph.Core.Framework;

// NuGet-free executable tests: dotnet run --project tests/Seraph.Core.Tests -c Release.
// Assertions target conservation, numerical behavior and public guarantees, not implementation layout.
internal static class Program
{
    private static readonly BodyProfile Reference = BodyProfile.Default;
    private static readonly BodyProfile NoBasal = BodyProfile.Default with { BasalMetabolicPowerW = 0 };
    private static readonly ModelOptions Closed = new()
    {
        EnableDryHeatExchange = false,
        EnableRespiration = false,
        EnableEvaporation = false,
        EnableSweating = false,
        EnableInsensibleWaterLoss = false,
        EnableFecalWaterLoss = false,
        EnablePlasmaRedistribution = false,
        MaximumShiveringPowerW = 0
    };

    private static int Main(string[] args)
    {
        (string Name, Action Test)[] tests =
        [
            ("Release food carriers, body mass, empty-gut recovery and allocation budget", ReleaseDigestionChecks.Run),
            ("Three-day reserves, linear intake, timed deficiency and bounded immunity", DietarySettlementChecks.ReservesAndDeficiency),
            ("Independent digestion, throughput, blocked stores and partition conservation", DietarySettlementChecks.IndependentFlows),
            ("Sleep slows food while water absorption continues", DietarySettlementChecks.SleepDigestion),
            ("Severe dehydration can recover from stomach water independently of food digestion", HydrationRecoveryChecks.Recovery),
            ("Solar exposure, sweat cap and nutrition support preserve thermal and water balance", ThermalHydrationChecks.Run),
            ("Immersion partitions air, evaporation and contact heat without duplicate surface area", ImmersionHeatChecks.Boundaries),
            ("Warm-water cooling works with low blood; cold water cools and hot water heats", ImmersionHeatChecks.Cooling),
            ("Adrenaline preserves resources while changing flow, pain, digestion and melee timing", AdrenalineChecks.Effects),
            ("Bounded running, reserve curves, jogging and phase partitions", RunningChecks.BoundedRunning),
            ("Burst transitions, held-key protection, empty-reserve running and prediction", RunningChecks.PaceAndReserves),
            ("Real-time lactate, independent soreness, partitions and save defaults", RunningChecks.LactateAndSoreness),
            ("Humidity follows weather and room temperature", HumidityChecks.Weather),
            ("Ketosis follows sustained accepted fuel, partitions, recovery and strongest limitation", KetosisChecks.SustainedFuel),
            ("Measured smoke dose uses breathing, real time, clean-air clearance and strongest cause", SmokeDoseChecks.Dose),
            ("Native maximum health preserves deficits and runtime-only bonuses", NativeModifierChecks.Health),
            ("Native hunger scales paid energy once without changing work or hydration", NativeModifierChecks.Energy),
            ("Native healing scales proposals but respects actual materials", NativeModifierChecks.Healing),
            ("Work-rest-work segmentation and frequent receipts conserve labor and lactate", BalanceChecks.WorkRestPartitions),
            ("Published flows include short work, rest, actual fuel and recovery units", BalanceChecks.PublishedFlows),
            ("Severe bleeding persists without care; pressure, bandages, healing and mild clotting remain effective", ClinicalChecks.PersistentBleeding),
            ("Work prediction reconciles accepts, rejects, reordering, credit-before-notice and reset", ExertionChecks.Prediction),
            ("Nutrition extends stamina without a deficiency penalty and prediction matches", ExertionChecks.NutritionCapacity),
            ("Thermal adaptation requires demand and consumes conserved heat, water and fuel flows", AdaptationChecks.DemandAndEffects),
            ("Acute pain decays independently; residual pain follows paid repair and merges conserve", PainChecks.NaturalDecay),
            ("Regional HP preserves absolute wounds across maximum changes without limb overflow", InjuryChecks.AbsoluteHealth),
            ("Hydration stages, baseline loss and absorption preserve one account", HydrationChecks.Settlement),
            ("Legacy bladder water migrates safely and urine production stays disabled", HydrationChecks.UrineProduction),
            ("Bowel conserves dry residue, queues and full/partial defecation", BowelChecks.Conservation),
            ("Sanitation requires actual sources and protects without partition drift", SanitationChecks.Sources),
            ("Nutrition stages and usable hydration projections preserve physical reserves", NutritionBalanceChecks.BandsAndDisplay),
            ("Meals digest over game hours with conserved, partition-independent flux", NutritionBalanceChecks.DigestionPace),
            ("Route barriers conserve identified agents and clear proportionally", ExposureBarrierChecks.CapacityAndClearance),
            ("Ambient clocks and cleared thresholds survive partitions and reload", ExposureBarrierChecks.AmbientAndHistory),
            ("Disease catalog and legacy agents retain verified exposure identity", DiseaseChecks.Sources),
            ("Regional cold exposure creates treatable frost injury only after sustained uncovered exposure", ColdExposureChecks.RegionalFrostbite),
            ("Food poisoning and diarrhea transfer mass once and preserve full-bowel queues", DiseaseChecks.FoodAndWater),
            ("Spoiled food response uses a 30% vomiting threshold and expels only the stomach queue", SpoilageChecks.Vomiting),
            ("Spoiled food dirtiness follows handling and eating thresholds", () =>
            {
                Near(0, SpoilageResponse.DirtinessGain(.1, true));
                Near(.02, SpoilageResponse.DirtinessGain(.2, false));
                Near(.03, SpoilageResponse.DirtinessGain(.2, true));
                Near(.05, SpoilageResponse.DirtinessGain(.45, false));
                Near(.08, SpoilageResponse.DirtinessGain(.45, true));
                Near(.10, SpoilageResponse.DirtinessGain(.8, false));
                Near(.15, SpoilageResponse.DirtinessGain(1, true));
            }),
            ("Drinking temperature bounds and neutral range", () =>
            {
                True(!DrinkingTemperature.IsDrinkable(-6) && !DrinkingTemperature.IsDrinkable(51));
                True(DrinkingTemperature.IsDrinkable(-5) && DrinkingTemperature.IsDrinkable(0)
                    && DrinkingTemperature.IsDrinkable(20) && DrinkingTemperature.IsDrinkable(50));
                Near(1, DrinkingTemperature.AbsorptionMultiplier(-5));
                Near(1, DrinkingTemperature.AbsorptionMultiplier(20));
                Near(1, DrinkingTemperature.AbsorptionMultiplier(50));
                Near(0, DrinkingTemperature.ThermalStimulusC(-5));
                Near(0, DrinkingTemperature.ThermalStimulusC(20));
                Near(0, DrinkingTemperature.ThermalStimulusC(50));
            }),
            ("Fever adjusts thermoregulation; sepsis requires sustained severe infection and danger", DiseaseChecks.FeverAndSepsis),
            ("Insect landing and bites survive partition, reload and washing", DiseaseChecks.Vectors),
            ("Respiratory contact accumulates by distance, ventilation and declared protection", DiseaseChecks.RespiratoryTransmission),
            ("Blood solute production, filtration, capacity and persistence conserve", CirculationChecks.Flows),
            ("Paid muscle/healing waste, proportional water absorption, partial rest and release saves", CirculationChecks.WasteSources),
            ("Blood regeneration pays real protein and fuel without duplicate water", CirculationChecks.Regeneration),
            ("Electrolytes strengthen renal filtration during blood toxicity without being consumed", CirculationChecks.RenalClearance),
            ("Six useful functions remain stable under short reserves and reach actual action/absorption costs", BodyFunctionChecks.StableAndUseful),
            ("Effective immunity governs susceptibility, disease and recovery without duplicate dirt penalties", BodyFunctionChecks.InfectionAndRecovery),
            ("All wound types support care, coagulation and material-paid regional repair", WoundRecoveryChecks.AllKinds),
            ("Stomach combines food and water in grams without duplicate food moisture", NutritionChecks.ContentsMass),
            ("Healing, immune support and accepted heat rates match simulation", MetricProjectionChecks.Rates),
            ("Environmental agents retain identity and 12/24/72-hour half lives", PollutionChecks.AgentPersistence),
            ("Agent concentrations survive mixing, filtering, heat and persistence", PathogenChecks.Conservation),
            ("Generic enteric contamination does not duplicate identified agents", PathogenChecks.GenericEntericDeduplication),
            ("Exposure clearance and acquired memory use persisted clocks", PathogenChecks.Memory),
            ("Real-time exertion costs, air floor, cooldown and sprint latch", ExertionChecks.Reserves),
            ("Native work receipts reject replay and bound elapsed mining/attack cycles", NativeWorkChecks.Receipts),
            ("Crop exposure conserves identified and generic contamination across partitions", CalendarChecks.CropPartition),
            ("Calendar debt survives bounded rounds and pause", CalendarChecks.Debt),
            ("Native sleep boundaries preserve calendar partitions and reset cleanly", CalendarSleepChecks.Boundaries),
            ("Actual action energy survives fast-forward and bounded partition", CalendarChecks.ActionEnergy),
            ("Game-hour rest, digestion and paid-work rates", CalendarChecks.Rates),
            ("Seven daily exploration and sleep cycles", CalendarChecks.WeeklySleep),
            ("Continuous exposure, sleep support and skin discomfort", TreatmentChecks.ExposureAndSupport),
            ("Medicine clocks, targets, caps and clinical disable", TreatmentChecks.TimingAndCaps),
            ("Dose refresh, overdose, replay and legacy persistence", TreatmentChecks.RepeatAndPersistence),
            ("Local wound targets and bounded exposure retention", TreatmentChecks.LocalWoundsAndCapacity),
            ("Distilled water and salt conservation through saturation and dryout", DistillationChecks.Conservation),
            ("Surface freshwater baseline doses, boiling and mixing", SurfaceWaterChecks.Sources),
            ("Weekly drinking risk retains intestinal and immune protection", WaterRiskChecks.Drinking),
            ("Closed pond mixing conserves, dilutes and persists identified contamination", WaterRiskChecks.PondTransport),
            ("Release water identity rejects old formats and preserves current doses", WaterCompatibilityChecks.ReleaseFormat),
            ("Boiling threshold, evaporation, residual solutes and recontamination", WaterHeatingChecks.Boiling),
            ("Bath water cools and reduces pathogens by temperature without removing other pollution", WaterHeatingChecks.BathTreatment),
            ("Large water batches mix above 10,000 L, conserve quality and retain volume bounds", WaterBatchChecks.LargeReservoir),
            ("Defecation contaminates hands; clean and dirty produce washing transfer distinct pathogen loads", HandContaminationChecks.Transfers),
            ("Well shaft capacity, gradual refill and regional water conservation", WellReservoirChecks.Storage),
            ("Filtration preserves salt, heat and volume with distinct media effects", FiltrationChecks.Treatment),
            ("Filtration flow respects receiver, water, media and tick bounds", FiltrationChecks.FlowBounds),
            ("Vegetation survey resumes within budget and sees ground under tall trees", PollutionChecks.VegetationSurvey),
            ("Fuel types produce distinct gases without biological exposure", PollutionChecks.FuelSignatures),
            ("Rain runoff and infiltration carry waste pathogens and nutrients", PollutionChecks.RunoffAndInfiltration),
            ("Wind, forest, rainfall and source removal clear pollution", PollutionChecks.DispersionAndRecovery),
            ("Measured smoke transports gas exposure without duplicate regional scrub or CO toxicity", PollutionChecks.SmokeCoupledGasTransport),
            ("Land use, regrowth and extraction affect persistent ecology", PollutionChecks.EcologyAndExtraction),
            ("CO affects body functions and clears in clean air", PollutionChecks.ExposureAndFunction),
            ("Chemical air exposure and route preview keep separate channels", PollutionChecks.ExposureAccounting),
            ("Water risk respects serving dose and chemical mixing", PollutionChecks.WaterDoseAndChemistry),
            ("Pollution rejects invalid persistence and bounds extreme sources", PollutionChecks.Bounds),
            ("Dehydration and hunger affect fatigue and gate high sleep pressure", RestChecks.Stress),
            ("Current sleep pressure clears legacy blur; verified intake grants temporary relief", RestChecks.PersistentBlur),
            ("Fatigue movement anchors preserve 85–100 percent without duplicate endurance", RestChecks.Movement),
            ("Held pressure and bandage affect exactly one wound", ClinicalChecks.SinglePressure),
            ("Aggregated wounds retain unique treatment identities", ClinicalChecks.CapacityIdentity),
            ("Enteric water loss conserves and cannot overdraw blood water", ClinicalChecks.EntericConservation),
            ("Pathogen exposure and disease course are time-consistent", ClinicalChecks.DiseaseCourse),
            ("Adrenaline trigger, ability window, cooldown and fatigue debt", ClinicalChecks.Adrenaline),
            ("Only known deficiencies cause specific clinical effects", ClinicalChecks.NutritionSymptoms),
            ("Systemic damage uses the strongest cause once", ClinicalChecks.SystemicDamage),
            ("Heat stress shares hydration, fever-offset and dangerous-temperature boundaries", ClinicalChecks.HeatStress),
            ("Cleaning reduces contamination without curing infection", ClinicalChecks.Cleaning),
            ("Automatic washing requires dirt or an unclean wound and avoids immediate repeats", ClinicalChecks.WashingNeed),
            ("Hit geometry preserves anatomy across four facings", InjuryChecks.Facing),
            ("Limb injuries directly affect the relevant actions", InjuryChecks.SideIndependence),
            ("Minor injuries do not disable native actions", InjuryChecks.MinorInjuries),
            ("Injury type and severity determine action thresholds", InjuryChecks.TypeAndThreshold),
            ("Chest injury changes exertion without duplicate damage", InjuryChecks.TorsoAndRespiration),
            ("Healing restores disabled functions", InjuryChecks.Recovery),
            ("Unlocated injury cannot fabricate local impairment", InjuryChecks.Unlocated),
            ("Correlated injury symptoms do not multiply mobility loss", InjuryChecks.CorrelatedPenalty),
            ("Injury events reject replay and unsupported diagnosis", InjuryChecks.ReplayAndValidation),
            ("Wound capacity retains new events and summarizes explicitly", InjuryChecks.Capacity),
            ("Injury function outputs remain bounded", InjuryChecks.Bounds),
            ("Reference profile and compartment invariant", ReferenceProfile),
            ("Internal heat transfer conserves total heat", InternalHeatConservation),
            ("Fuel heat and external work share one ledger", MetabolicConservation),
            ("Unavailable fuel cannot create shivering or work", ExhaustedFuel),
            ("Blood loss uses constituent volumes once", BleedingConservation),
            ("Blood loss cannot overdraw empty constituents", ExhaustedBlood),
            ("Plasma redistribution changes no total water or red cells", PlasmaRedistribution),
            ("Protected blood water is not ordinary loss inventory", ExhaustedAccessibleWater),
            ("Evaporation consumes film once and removes latent heat", FilmEvaporation),
            ("Rain is external film water", RainWater),
            ("Skin baseline water is a transfer before evaporation", InsensibleFilmTransfer),
            ("Intake queues, capacities and absorption conserve", IntakeAndAbsorption),
            ("Full water stores retain water; absorbed excess fuel dissipates as heat", FullStoreAbsorption),
            ("Sleep pressure follows elapsed time and quality", SleepDynamics),
            ("Invalid inputs retain the exact prior snapshot", InvalidInputs),
            ("Invalid profiles and settings fail at construction", InvalidConfiguration),
            ("Zero elapsed time is a no-op", ZeroTime),
            ("Halving integration step converges", NumericalConvergence),
            ("Clothing, wind and inward heat have consistent signs", DryBoundaryResponse),
            ("Clothing display uses the actual heat ledger without settlement", HeatDisplayChecks.SameBoundary),
            ("Calendar cooling follows core/skin heat capacities without a second calendar divisor", HeatDisplayChecks.CalendarCooling),
            ("Equal internal substeps are independent of caller batching", BatchIndependence),
            ("Vapor approximation reports its fit boundary", VaporApproximationBoundary),
            ("Varied boundary conditions keep ledgers balanced", VariedScenario),
            ("Large callback time has bounded backlog and explicit loss", BoundedScheduler),
            ("Pause discards pending time without catch-up", PausedScheduler),
            ("Invalid scheduler delta retains backlog", InvalidScheduler),
            ("Configured speed preserves bounded integration steps", SpeedScheduler),
            ("Scheduler work cap retains bounded pending time", SchedulerWorkLimit)
            ,("Virtual groundwater derivation is deterministic", GroundwaterDeterminism)
            ,("Groundwater extraction and evaporation conserve storage", GroundwaterConservation)
            ,("Desalination separates fresh product and brine", Desalination)
            ,("Room heat is bounded and open rooms follow climate", RoomThermalDirections)
            ,("Equivalent temperature reflects wind, radiant heat and water contact", EquivalentTemperatureDirections)
            ,("Groundwater material reference ranges are bounded", GroundwaterMaterialRanges)
            ,("Groundwater layers conserve local storage", GroundwaterLayerConservation)
            ,("Groundwater hydraulic exchange conserves both cells", GroundwaterExchange)
            ,("Groundwater hydraulic exchange follows head", GroundwaterExchangeDirection)
            ,("Groundwater type thresholds and thickness are bounded", GroundwaterTypes)
            ,("Bladder capacity, partial urination and validation", PhysiologyExtensionChecks.Bladder)
            ,("Clinical indicators respond to exertion and hypoxia", PhysiologyExtensionChecks.Clinical)
            ,("Soap cleans all open wounds without curing infection", SoapCareChecks.Run)
            ,("Ash and manure fertilizer doses are bounded and distinct", PhysiologyExtensionChecks.AshFertilizer)
            ,("Energy compartments do not confuse fat with readiness", NutritionChecks.EnergyCompartments)
            ,("Sufficient reserves do not require full capacity", NutritionChecks.AdequateReserves)
            ,("Absorption interactions cannot create nutrients", NutritionChecks.AbsorptionConservation)
            ,("Unknown nutrition is neutral and safely addressable", NutritionChecks.UnknownNutrition)
            ,("Nutrition input rejection and zero time preserve state", NutritionChecks.InputBoundaries)
            ,("Positive effects are bounded and do not stack with excess", NutritionChecks.PositiveEffects)
            ,("Independent support survives individual body stress", NutritionChecks.StressGates)
            ,("Known deficiencies retain meaningful penalties", NutritionChecks.DeficiencyPenalties)
            ,("Nutrition trend follows composition rather than body fat", NutritionChecks.CompositionTrend)
            ,("Supported recovery changes actual framework outcomes", NutritionChecks.FrameworkRecovery)
            ,("Recovery benefits do not waive activity costs", NutritionChecks.ActivityCosts)
            ,("Unavailable oxygen evidence does not fabricate organ impairment", NutritionChecks.OxygenObservation)
            ,("Physical absorption remains valid at the nutrition boundary", NutritionChecks.PhysicalAbsorptionBoundary)
            ,("Micronutrients retain equal daily decay without hidden vitamin D dependence", NutritionChecks.MicronutrientRatesAndVitaminDIndependence)
            ,("Fiber occupies the stomach, passes slowly without energy and conserves bowel mass", NutritionChecks.FiberPassage)
            ,("Nutrition and fatigue preserve native jump clearance", NutritionChecks.JumpFloor)
            ,("Muscle and electrolyte reserves respond to activity and intake", PhysiologyEnhancementChecks.MuscleAndElectrolytes)
            ,("Low glycogen uses fat; muscle growth costs protein/fuel and benefits force actions", PhysiologyEnhancementChecks.TrainingAndFuel)
            ,("Sleep quality and immunity respond to bed and environment", PhysiologyEnhancementChecks.SleepAndImmunity)
            ,("Environmental contamination raises bounded pathogen exposure", PhysiologyEnhancementChecks.EnvironmentalContaminationRaisesExposure)
            ,("Slow resource deficits reach endurance without duplicate mining or movement penalties", PhysiologyEnhancementChecks.ResourceCapacity)
            ,("Low oxygen raises action stamina and fatigue cost", PhysiologyEnhancementChecks.OxygenRaisesActionCost)
        ];
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ReferenceProfile()
    {
        True(Reference.Validate() is null);
        var state = BodyState.Create();
        True(state.Validate(Reference) is null);
        Near(5, state.BloodVolumeL(Reference), 1e-12);
        Near(245000, Reference.TotalHeatCapacityJPerK, 1e-12);
        True((state with { BodyWaterL = 1 }).Validate(Reference) is not null);
        True((state with { CoreTemperatureC = double.NaN }).Validate(Reference) is not null);
        True((state with { GutWaterL = 6 }).Validate(Reference) is not null);
    }

    private static void InternalHeatConservation()
    {
        var initial = BodyState.Create(NoBasal) with { CoreTemperatureC = 39, SkinTemperatureC = 31 };
        var result = Advance(new SimulationKernel(NoBasal, Closed), initial, 8);
        True(result.State.CoreTemperatureC < initial.CoreTemperatureC);
        True(result.State.SkinTemperatureC > initial.SkinTemperatureC);
        True(result.Diagnostics.CoreToSkinHeatJ > 0);
        Near(0, HeatChange(initial, result.State, NoBasal), 1e-6);
        Balanced(result.Diagnostics);
    }

    private static void MetabolicConservation()
    {
        var initial = BodyState.Create();
        var result = Advance(new SimulationKernel(Reference, Closed), initial, 8,
            inputs: new StepInputs { ActivityPowerW = 400 });
        Near((Reference.BasalMetabolicPowerW + 400) * 8, result.Diagnostics.MetabolicEnergyJ, 1e-8);
        Near(640, result.Diagnostics.ExternalWorkJ, 1e-8);
        Near((Reference.BasalMetabolicPowerW + 400) * 8 - 640, HeatChange(initial, result.State, Reference), 1e-6);
        Near(result.Diagnostics.MetabolicEnergyJ,
            result.Diagnostics.BasalEnergyJ + result.Diagnostics.ShiveringEnergyJ + result.Diagnostics.ActivityEnergyJ, 1e-8);
        Balanced(result.Diagnostics);
    }

    private static void ExhaustedFuel()
    {
        var initial = BodyState.Create() with
        {
            GlycogenEnergyKJ = 0, FatEnergyKJ = 0, MuscleMassG = 0,
            CoreTemperatureC = 35, SkinTemperatureC = 20
        };
        var result = Advance(new SimulationKernel(Reference, Closed with { MaximumShiveringPowerW = 300 }),
            initial, 8, inputs: new StepInputs { ActivityPowerW = 1000 });
        Near(0, result.Diagnostics.MetabolicEnergyJ);
        Near(0, result.Diagnostics.ShiveringEnergyJ);
        Near(0, result.Diagnostics.ExternalWorkJ);
        True(result.Diagnostics.ResourceLimited && result.Diagnostics.UnmetMetabolicEnergyJ > 0);
        Near(0, HeatChange(initial, result.State, Reference), 1e-6);
        Near(0, result.State.GlycogenEnergyKJ);
        Near(0, result.State.FatEnergyKJ);
        Balanced(result.Diagnostics);
    }

    private static void BleedingConservation()
    {
        var initial = BodyState.Create(NoBasal);
        var result = Advance(new SimulationKernel(NoBasal, Closed), initial, 2,
            inputs: new StepInputs { BleedLps = 0.05 });
        Near(0.1, result.Diagnostics.WholeBloodLossL, 1e-12);
        Near(0.045, initial.RbcVolumeL - result.State.RbcVolumeL, 1e-12);
        Near(0.0506, initial.PlasmaWaterL - result.State.PlasmaWaterL, 1e-12);
        Near(0.0821, initial.BodyWaterL - result.State.BodyWaterL, 1e-12);
        Near(4.9, result.State.BloodVolumeL(NoBasal), 1e-12);
        Balanced(result.Diagnostics);
    }

    private static void ExhaustedBlood()
    {
        var result = Advance(new SimulationKernel(NoBasal, Closed), BodyState.Create(NoBasal), 8,
            inputs: new StepInputs { BleedLps = 1 });
        Near(5, result.Diagnostics.WholeBloodLossL, 1e-10);
        Near(0, result.State.RbcVolumeL, 1e-10);
        Near(0, result.State.PlasmaWaterL, 1e-10);
        Near(37.895, result.State.BodyWaterL, 1e-10);
        True(result.State.Validate(NoBasal) is null);
        Balanced(result.Diagnostics);
    }

    private static void ExhaustedAccessibleWater()
    {
        var initial = BodyState.Create(NoBasal) with
        {
            CoreTemperatureC = 39, SkinTemperatureC = 36,
            BodyWaterL = NoBasal.ReferencePlasmaWaterL + NoBasal.RbcWaterFraction * NoBasal.ReferenceRbcVolumeL
        };
        var options = Closed with
        {
            EnableRespiration = true, EnableSweating = true,
            EnableFecalWaterLoss = true, EnableInsensibleWaterLoss = true
        };
        var result = Advance(new SimulationKernel(NoBasal, options), initial, 8);
        Near(initial.BodyWaterL, result.State.BodyWaterL, 1e-12);
        Near(0, result.Diagnostics.RespiratoryWaterL);
        Near(0, result.Diagnostics.FecalWaterL);
        True(result.Diagnostics.ResourceLimited);
        True(result.State.Validate(NoBasal) is null);
        Balanced(result.Diagnostics);
    }

    private static void PlasmaRedistribution()
    {
        var kernel = new SimulationKernel(NoBasal, Closed with { EnablePlasmaRedistribution = true });
        var initial = BodyState.Create(NoBasal) with { PlasmaWaterL = 1 };
        var replenished = Advance(kernel, initial, 8);
        True(replenished.State.PlasmaWaterL > initial.PlasmaWaterL);
        Near(initial.BodyWaterL, replenished.State.BodyWaterL);
        Near(initial.RbcVolumeL, replenished.State.RbcVolumeL);
        Balanced(replenished.Diagnostics);
        var depleted = initial with { BodyWaterL = initial.PlasmaWaterL + NoBasal.RbcWaterFraction * initial.RbcVolumeL };
        var redistributed = Advance(kernel, depleted, 8);
        True(redistributed.State.PlasmaWaterL < depleted.PlasmaWaterL);
        Near(depleted.BodyWaterL, redistributed.State.BodyWaterL);
        Near(depleted.RbcVolumeL, redistributed.State.RbcVolumeL);
        True(redistributed.State.Validate(NoBasal) is null);
        Balanced(redistributed.Diagnostics);
    }

    private static void FilmEvaporation()
    {
        var initial = BodyState.Create(NoBasal) with { SkinFilmWaterKg = 0.01 };
        var result = Advance(new SimulationKernel(NoBasal, Closed with { EnableEvaporation = true }), initial, 8);
        True(result.Diagnostics.EvaporatedWaterKg > 0 && result.Diagnostics.EvaporatedWaterKg <= 0.01);
        Near(initial.BodyWaterL, result.State.BodyWaterL, 1e-12);
        Near(initial.SkinFilmWaterKg - result.State.SkinFilmWaterKg, result.Diagnostics.EvaporatedWaterKg, 1e-12);
        Near(result.Diagnostics.EvaporatedWaterKg * 2.4e6, result.Diagnostics.EvaporativeHeatOutJ, 1e-8);
        Near(-result.Diagnostics.EvaporativeHeatOutJ, HeatChange(initial, result.State, NoBasal), 1e-6);
        Balanced(result.Diagnostics);
    }

    private static void RainWater()
    {
        var initial = BodyState.Create(NoBasal);
        var environment = EnvironmentSample.Neutral with
        {
            AirTemperatureC = 35, RelativeHumidity = 1, RainFilmInflowKgPerSecond = 0.1
        };
        var result = Advance(new SimulationKernel(NoBasal, Closed with { EnableEvaporation = true }), initial, 2, environment);
        Near(0.2, result.State.SkinFilmWaterKg, 1e-12);
        Near(initial.BodyWaterL, result.State.BodyWaterL, 1e-12);
        Near(0, result.Diagnostics.EvaporatedWaterKg);
        Balanced(result.Diagnostics);

        var overflow = Advance(new SimulationKernel(NoBasal, Closed), initial, 8,
            environment with { RainFilmInflowKgPerSecond = 1 });
        Near(NoBasal.MaximumSkinFilmWaterKg, overflow.State.SkinFilmWaterKg);
        Near(6, overflow.Diagnostics.RunoffWaterKg);
        Balanced(overflow.Diagnostics);
    }

    private static void InsensibleFilmTransfer()
    {
        var initial = BodyState.Create(NoBasal);
        var result = Advance(new SimulationKernel(NoBasal, Closed with { EnableInsensibleWaterLoss = true }), initial, 8);
        True(result.Diagnostics.InsensibleWaterL is > 0 and < 4.8e-5);
        Near(result.Diagnostics.InsensibleWaterL, result.State.SkinFilmWaterKg, 1e-12);
        Near(result.Diagnostics.InsensibleWaterL, initial.BodyWaterL - result.State.BodyWaterL, 1e-12);
        Balanced(result.Diagnostics);
    }

    private static void IntakeAndAbsorption()
    {
        var kernel = new SimulationKernel(NoBasal, Closed);
        var initial = BodyState.Create(NoBasal) with { BodyWaterL = NoBasal.ReferenceBodyWaterL - 1 };
        var intake = kernel.ApplyIntake(initial, new IntakeEvent { WaterL = 6, EnergyKJ = 25000 });
        True(intake.Accepted);
        Near(5, intake.AcceptedWaterL);
        Near(1, intake.RejectedWaterL);
        Near(25000, intake.AcceptedEnergyKJ);
        Near(0, intake.RejectedEnergyKJ);
        Near(initial.BodyWaterL, intake.State.BodyWaterL);
        var result = Advance(kernel, intake.State, 8);
        Near(2000d / 1000 / 3600 * 8, result.Diagnostics.AbsorbedWaterL, 1e-12);
        Near(25000 * (1 - Math.Pow(2, -8d / 5400)), result.Diagnostics.AbsorbedEnergyKJ, 1e-9);
        Near(5, result.State.GutWaterL + result.State.BodyWaterL - initial.BodyWaterL, 1e-10);
        Balanced(result.Diagnostics);
        var invalid = kernel.ApplyIntake(initial, new IntakeEvent { WaterL = double.NaN });
        True(!invalid.Accepted && ReferenceEquals(initial, invalid.State));
    }

    private static void FullStoreAbsorption()
    {
        var initial = BodyState.Create(NoBasal) with
        {
            BodyWaterL = NoBasal.MaximumBodyWaterL,
            GlycogenEnergyKJ = NoBasal.GlycogenCapacityKJ,
            FatEnergyKJ = NoBasal.FatCapacityKJ,
            GutWaterL = 1, GutEnergyKJ = 10
        };
        var result = Advance(new SimulationKernel(NoBasal, Closed), initial, 8);
        Near(1, result.State.GutWaterL);
        Near(10, result.State.GutEnergyKJ + result.Diagnostics.AbsorbedEnergyKJ);
        Near(0, result.Diagnostics.AbsorbedWaterL);
        Near(result.Diagnostics.AbsorbedEnergyKJ * 1000, result.Diagnostics.ConversionHeatJ);
        Balanced(result.Diagnostics);
    }

    private static void SleepDynamics()
    {
        var kernel = new SimulationKernel(NoBasal, Closed);
        var initial = BodyState.Create(NoBasal);
        var awake = Advance(kernel, initial, 8);
        var defaults = new StepInputs();
        Near(initial.SleepPressure + 8.0 / defaults.SleepPressureRiseSeconds, awake.State.SleepPressure, 1e-13);
        var sleepInputs = new StepInputs { IsSleeping = true, SleepQuality = 0.75 };
        var sleep = Advance(kernel, initial, 8, inputs: sleepInputs);
        Near(initial.SleepPressure - sleepInputs.SleepQuality * 8 / sleepInputs.SleepPressureRecoverySeconds, sleep.State.SleepPressure, 1e-13);
        var noQuality = Advance(kernel, initial, 8, inputs: new StepInputs { IsSleeping = true, SleepQuality = 0 });
        Near(initial.SleepPressure, noQuality.State.SleepPressure);
        Near(8, sleep.State.ElapsedBioSeconds);
    }

    private static void InvalidInputs()
    {
        var kernel = new SimulationKernel(Reference);
        var initial = BodyState.Create();
        foreach (var dt in new[] { -1, double.NaN, double.PositiveInfinity, 8.001 })
            Rejected(kernel.Advance(initial, EnvironmentSample.Neutral, new StepInputs(), dt), initial);
        foreach (var environment in new[]
        {
            EnvironmentSample.Neutral with { IsValid = false },
            EnvironmentSample.Neutral with { RelativeHumidity = 1.01 },
            EnvironmentSample.Neutral with { WindSpeedMps = double.NaN },
            EnvironmentSample.Neutral with { AirTemperatureC = double.NegativeInfinity },
            EnvironmentSample.Neutral with { ClothingInsulationClo = -1 }
        }) Rejected(kernel.Advance(initial, environment, new StepInputs(), 1), initial);
        foreach (var inputs in new[]
        {
            new StepInputs { ActivityPowerW = -1 }, new StepInputs { SleepQuality = 1.1 },
            new StepInputs { BleedLps = double.NaN }
        }) Rejected(kernel.Advance(initial, EnvironmentSample.Neutral, inputs, 1), initial);
        var invalidState = initial with { BodyWaterL = 0 };
        Rejected(kernel.Advance(invalidState, EnvironmentSample.Neutral, new StepInputs(), 1), invalidState);
        var unstableBoundary = EnvironmentSample.Neutral with { ContactConductanceWPerK = 10000, ContactTemperatureC = -80 };
        var tinyCapacity = Reference with { MassKg = 1, SpecificHeatJPerKgK = 100, PeripheralHeatCapacityFraction = 0.01 };
        var lastGood = BodyState.Create(tinyCapacity);
        Rejected(new SimulationKernel(tinyCapacity).Advance(lastGood, unstableBoundary, new StepInputs(), 8), lastGood);
    }

    private static void InvalidConfiguration()
    {
        Throws<ArgumentException>(() => new SimulationKernel(Reference with { PlasmaWaterFraction = 0 }));
        Throws<ArgumentException>(() => new SimulationKernel(Reference, new ModelOptions { MaximumSubstepBioSeconds = 1 }));
        Throws<ArgumentException>(() => new BoundedTimeScheduler(new SchedulerOptions { PhysiologySpeed = 20.1 }));
        Throws<ArgumentException>(() => BodyState.Create(Reference with { InitialFatEnergyKJ = double.NaN }));
    }

    private static void ZeroTime()
    {
        var initial = BodyState.Create();
        var result = Advance(new SimulationKernel(Reference), initial, 0);
        True(ReferenceEquals(initial, result.State));
        Near(0, result.Diagnostics.AcceptedBioSeconds);
        True(result.Diagnostics.Substeps == 0);
    }

    private static void NumericalConvergence()
    {
        BodyState Run(double step)
        {
            var kernel = new SimulationKernel(Reference, new ModelOptions
            {
                MaximumSubstepBioSeconds = step, MaximumAdvanceBioSeconds = 1
            });
            var state = BodyState.Create();
            var environment = EnvironmentSample.Neutral with
            {
                AirTemperatureC = 0, RadiantTemperatureC = 0, WindSpeedMps = 2, ClothingInsulationClo = 0
            };
            for (var second = 0; second < 120; second++) state = Advance(kernel, state, 1, environment).State;
            return state;
        }
        var coarse = Run(0.25);
        var fine = Run(0.125);
        var finest = Run(0.0625);
        var coarseError = Math.Abs(coarse.SkinTemperatureC - finest.SkinTemperatureC)
            + Math.Abs(coarse.CoreTemperatureC - finest.CoreTemperatureC);
        var fineError = Math.Abs(fine.SkinTemperatureC - finest.SkinTemperatureC)
            + Math.Abs(fine.CoreTemperatureC - finest.CoreTemperatureC);
        True(fineError <= coarseError * 0.75 + 1e-6, $"halving step did not converge: {coarseError} vs {fineError}");
        True(coarseError < 0.02, $"step error too large: {coarseError}");
    }

    private static void VariedScenario()
    {
        var kernel = new SimulationKernel(Reference);
        var state = BodyState.Create();
        var random = new Random(14371);
        for (var index = 0; index < 512; index++)
        {
            var environment = EnvironmentSample.Neutral with
            {
                AirTemperatureC = -30 + 75 * random.NextDouble(),
                RadiantTemperatureC = -30 + 75 * random.NextDouble(),
                RelativeHumidity = random.NextDouble(),
                WindSpeedMps = 10 * random.NextDouble(),
                ClothingInsulationClo = 3 * random.NextDouble(),
                RainFilmInflowKgPerSecond = 0.001 * random.NextDouble()
            };
            var inputs = new StepInputs
            {
                ActivityPowerW = 1000 * random.NextDouble(), BleedLps = 0.001 * random.NextDouble()
            };
            var result = Advance(kernel, state, 0.25, environment, inputs);
            True(result.State.Validate(Reference) is null);
            Balanced(result.Diagnostics);
            state = result.State;
        }
    }

    private static void DryBoundaryResponse()
    {
        var kernel = new SimulationKernel(NoBasal, Closed with { EnableDryHeatExchange = true });
        var state = BodyState.Create(NoBasal);
        var environment = EnvironmentSample.Neutral with
        {
            AirTemperatureC = 0, RadiantTemperatureC = 0, ClothingInsulationClo = 0
        };
        var bareCalm = Advance(kernel, state, 1, environment);
        var bareWindy = Advance(kernel, state, 1, environment with { WindSpeedMps = 10 });
        var clothed = Advance(kernel, state, 1, environment with { ClothingInsulationClo = 5 });
        True(bareCalm.Diagnostics.DryHeatOutJ > 0);
        True(bareWindy.Diagnostics.DryHeatOutJ > bareCalm.Diagnostics.DryHeatOutJ);
        True(clothed.Diagnostics.DryHeatOutJ < bareCalm.Diagnostics.DryHeatOutJ);
        var hot = Advance(kernel, state, 1,
            environment with { AirTemperatureC = 45, RadiantTemperatureC = 45 });
        True(hot.Diagnostics.DryHeatOutJ < 0);
        True(HeatChange(state, hot.State, NoBasal) > 0);
        foreach (var result in new[] { bareCalm, bareWindy, clothed, hot }) Balanced(result.Diagnostics);
    }

    private static void BatchIndependence()
    {
        var kernel = new SimulationKernel(Reference);
        var initial = BodyState.Create() with { GutWaterL = 1, GutEnergyKJ = 50 };
        var environment = EnvironmentSample.Neutral with { AirTemperatureC = -5, WindSpeedMps = 2 };
        var inputs = new StepInputs { ActivityPowerW = 300, BleedLps = 0.002, IsSleeping = true, SleepQuality = 0.5 };
        var batched = Advance(kernel, initial, 8, environment, inputs).State;
        var individual = initial;
        for (var index = 0; index < 32; index++) individual = Advance(kernel, individual, 0.25, environment, inputs).State;
        True(batched == individual, "Batching changed a deterministic state transition.");
    }

    private static void VaporApproximationBoundary()
    {
        var kernel = new SimulationKernel(Reference);
        var initial = BodyState.Create();
        var normal = Advance(kernel, initial, 0.25);
        True(!normal.Diagnostics.VaporPressureOutsideFitDomain);
        var cold = Advance(kernel, initial, 0.25,
            EnvironmentSample.Neutral with { AirTemperatureC = -50, RadiantTemperatureC = -50 });
        True(cold.Diagnostics.VaporPressureOutsideFitDomain);
        Balanced(cold.Diagnostics);
    }

    private static void BoundedScheduler()
    {
        var result = new BoundedTimeScheduler().Schedule(100);
        True(result.Accepted);
        Near(2, result.AcceptedBioSeconds);
        Near(95, result.DroppedServerSeconds);
        Near(3, result.PendingServerSeconds);
        True(result.SubstepCount == 8);
        Near(0.25, result.SubstepBioSeconds);
        var enormous = new BoundedTimeScheduler().Schedule(double.MaxValue);
        True(enormous.Accepted && double.IsFinite(enormous.DroppedServerSeconds));
        True(enormous.SubstepCount <= 32 && enormous.PendingServerSeconds <= 5);
    }

    private static void PausedScheduler()
    {
        var scheduler = new BoundedTimeScheduler();
        scheduler.Schedule(100);
        var paused = scheduler.Schedule(1, paused: true);
        Near(4, paused.DroppedServerSeconds);
        Near(0, paused.AcceptedBioSeconds);
        Near(0, paused.PendingServerSeconds);
        var resumed = scheduler.Schedule(0.25);
        Near(0.25, resumed.AcceptedBioSeconds);
        Near(0, resumed.PendingServerSeconds);
    }

    private static void InvalidScheduler()
    {
        var scheduler = new BoundedTimeScheduler();
        scheduler.Schedule(100);
        foreach (var dt in new[] { -1, double.NaN, double.PositiveInfinity })
        {
            var result = scheduler.Schedule(dt);
            True(!result.Accepted);
            Near(3, result.PendingServerSeconds);
            Near(0, result.AcceptedBioSeconds);
        }
        Near(2, scheduler.Schedule(0).AcceptedBioSeconds);
        Near(1, scheduler.Schedule(0).AcceptedBioSeconds);
        Near(0, scheduler.Schedule(0).AcceptedBioSeconds);
    }

    private static void SpeedScheduler()
    {
        foreach (var speed in new[] { 0.1, 1, 4, 10, 20 })
        {
            var scheduler = new BoundedTimeScheduler(new SchedulerOptions { PhysiologySpeed = speed });
            double total = 0;
            // Normal 250 ms server callbacks must sustain the selected speed without backlog.
            for (var tick = 0; tick < 240; tick++)
            {
                var result = scheduler.Schedule(0.25);
                True(result.Accepted);
                Near(0.25 * speed, result.AcceptedBioSeconds, 1e-12);
                True(result.SubstepCount is > 0 and <= 32);
                True(result.SubstepBioSeconds <= 0.25);
                Near(result.AcceptedBioSeconds, result.SubstepCount * result.SubstepBioSeconds, 1e-12);
                Near(0, result.PendingServerSeconds);
                Near(0, result.DroppedServerSeconds);
                total += result.AcceptedBioSeconds;
            }
            Near(60 * speed, total, 1e-9);
        }

        var accelerated = new BoundedTimeScheduler(new SchedulerOptions { PhysiologySpeed = 20 });
        var delayed = accelerated.Schedule(2);
        Near(8, delayed.AcceptedBioSeconds);
        Near(1.6, delayed.PendingServerSeconds);
        True(delayed.SubstepCount == 32);
        var stalled = accelerated.Schedule(100);
        True(stalled.SubstepCount == 32 && stalled.SubstepBioSeconds <= 0.25);
        Near(102, (delayed.AcceptedBioSeconds + stalled.AcceptedBioSeconds) / 20
            + stalled.PendingServerSeconds + stalled.DroppedServerSeconds, 1e-9);
        var paused = accelerated.Schedule(0, paused: true);
        Near(stalled.PendingServerSeconds, paused.DroppedServerSeconds);
        Near(0, accelerated.Schedule(0).AcceptedBioSeconds);
    }

    private static void SchedulerWorkLimit()
    {
        var scheduler = new BoundedTimeScheduler(new SchedulerOptions { MaximumSubstepsPerRound = 1 });
        var totalAccepted = 0.0;
        var totalDropped = 0.0;
        TimeBudgetPlan? last = null;
        for (var index = 0; index < 100; index++)
        {
            last = scheduler.Schedule(2);
            True(last.SubstepCount == 1 && last.SubstepBioSeconds <= 0.25);
            True(last.PendingServerSeconds <= 5);
            totalAccepted += last.AcceptedBioSeconds;
            totalDropped += last.DroppedServerSeconds;
        }
        Near(200, totalAccepted + totalDropped + last!.PendingServerSeconds, 1e-9);
    }

    private static void GroundwaterDeterminism()
    {
        var a = WaterCycleModel.DeriveCell(42, 10, -20, 30, 0.7, coastal: false, 12);
        var b = WaterCycleModel.DeriveCell(42, 10, -20, 30, 0.7, coastal: false, 12);
        True(a == b && a.Validate() is null);
        var coastal = WaterCycleModel.DeriveCell(42, 10, -20, 30, 0.7, coastal: true, 12);
        True(coastal.SalinityGPerL > a.SalinityGPerL);
    }

    private static void GroundwaterConservation()
    {
        var initial = new GroundwaterCell(100, 200, 0.2, 12);
        var step = WaterCycleModel.Advance(initial, new WaterCycleInputs(60, 20, 0.5, 3, 4, 50, 15, 0.4, 2, 0.5));
        Near(59, step.State.StorageL);
        Near(step.RechargedL - step.EvaporatedL - step.ExtractedL, step.NetChangeL);
        True(step.RejectedExtractionL == 0 && step.State.Validate() is null);
        var overdraw = WaterCycleModel.Advance(step.State, new WaterCycleInputs(1, 0, 0, 0, 0, 1000, 15, 0.5, 0, 0));
        Near(0, overdraw.State.StorageL);
        True(overdraw.RejectedExtractionL > 0);
    }

    private static void Desalination()
    {
        var source = new WaterBatch { VolumeL = 10, SaltGramsPerL = 35, TemperatureC = 20, Source = "salt", Unverified = true };
        var result = WaterTreatmentModel.Desalinate(source, 60, 90);
        True(result.Accepted && result.Product is not null && result.Brine is not null);
        var product = result.Product!;
        var brine = result.Brine!;
        Near(source.VolumeL, product.VolumeL + brine.VolumeL);
        True(product.SaltGramsPerL < source.SaltGramsPerL && brine.SaltGramsPerL > source.SaltGramsPerL);
        Near(source.VolumeL * source.SaltGramsPerL,
            product.VolumeL * product.SaltGramsPerL + brine.VolumeL * brine.SaltGramsPerL, 1e-10);
        True(WaterTreatmentModel.Desalinate(source, 1, 20).Accepted == false);
    }

    private static void RoomThermalDirections()
    {
        var open = new RoomThermalInputs(-10, 10, false, 0, 0, 27, 1);
        Near(-10, RoomTemperatureModel.TargetTemperature(open));
        var enclosed = open with { Enclosed = true, NonCoolingWallCount = 6, CoolingWallCount = 0 };
        var target = RoomTemperatureModel.TargetTemperature(enclosed);
        True(target > -10 && target <= 8);
        var advanced = RoomTemperatureModel.Advance(-10, enclosed, 4);
        True(advanced > -10 && advanced <= target);
        var custom = RoomTemperatureModel.Advance(-10, enclosed, 4, maxHeatBonusC: 2, celsiusPerStrength: 0.5);
        True(custom > -10 && custom < advanced);
    }

    private static void EquivalentTemperatureDirections()
    {
        var kernel = new SimulationKernel(BodyProfile.Default);
        var neutral = EnvironmentSample.Neutral;
        var coldWind = kernel.EquivalentTemperatureC(neutral with { AirTemperatureC = 0, RadiantTemperatureC = 0, WindSpeedMps = 8 });
        var still = kernel.EquivalentTemperatureC(neutral with { AirTemperatureC = 0, RadiantTemperatureC = 0, WindSpeedMps = 0 });
        var fire = kernel.EquivalentTemperatureC(neutral with { AirTemperatureC = 0, RadiantTemperatureC = 40, WindSpeedMps = 0 });
        var water = kernel.EquivalentTemperatureC(neutral with { AirTemperatureC = 20, RadiantTemperatureC = 20,
            ContactConductanceWPerK = 40, ContactTemperatureC = 5 });
        True(coldWind < 0 && still > coldWind && fire > still && water < 20);
    }

    private static void GroundwaterMaterialRanges()
    {
        foreach (var material in Enum.GetValues<AquiferMaterialClass>())
        {
            var properties = AquiferMaterials.Properties(material);
            True(properties.Validate() is null);
            True(properties.DrainablePorosity is >= 0.01 and <= 0.30);
        }
    }

    private static void GroundwaterLayerConservation()
    {
        var state = GroundwaterHydrologyModel.Derive(42, 4, 9, 20, 0.7, 12, AquiferMaterialClass.CoarseSand);
        var step = GroundwaterHydrologyModel.Advance(state, new GroundwaterInputs(3600, 120, 20, 2, 5, 40, 15, 0.5, 5, 16));
        Near(step.State.StorageL, state.StorageL + step.NetLocalChangeL, 1e-8);
        True(step.State.StorageL >= 0 && step.State.StorageL <= step.State.CapacityL);
        True(step.State.WaterTableDepthM >= 0);
    }

    private static void GroundwaterExchange()
    {
        var a = GroundwaterHydrologyModel.Derive(7, 1, 1, 10, 0.5, 10, AquiferMaterialClass.Gravel);
        var b = a with { StorageL = a.CapacityL * 0.8, WaterTableDepthM = 2 };
        var transfer = GroundwaterHydrologyModel.Exchange(a, b, 3600, 16);
        Near(a.StorageL + b.StorageL, transfer.A.StorageL + transfer.B.StorageL, 1e-8);
        True(transfer.AToBL >= 0 && transfer.BToAL >= 0);
        True(transfer.A.Validate() is null && transfer.B.Validate() is null);
    }

    private static void GroundwaterTypes()
    {
        var state = GroundwaterHydrologyModel.Derive(7, 1, 1, 10, 0.5, 10, AquiferMaterialClass.FineSand);
        True(state.ThicknessM is >= 1 and <= 200);
        True(state.WaterTableDepthM <= state.ThicknessM);
        True((state with { SalinityGPerL = 0.2 }).Type == GroundwaterType.Fresh);
        True((state with { SalinityGPerL = 5 }).Type == GroundwaterType.Brackish);
        True((state with { SalinityGPerL = 35 }).Type == GroundwaterType.Saline);
    }

    private static void GroundwaterExchangeDirection()
    {
        var highHead = GroundwaterHydrologyModel.Derive(19, 1, 1, 10, 0.7, 12, AquiferMaterialClass.Gravel)
            with { StorageL = 0.8 * GroundwaterHydrologyModel.Derive(19, 1, 1, 10, 0.7, 12, AquiferMaterialClass.Gravel).CapacityL, WaterTableDepthM = 1 };
        var lowHead = highHead with { StorageL = 0.2 * highHead.CapacityL, WaterTableDepthM = 10 };
        var transfer = GroundwaterHydrologyModel.Exchange(highHead, lowHead, 3600, 16);
        True(transfer.AToBL > 0 && transfer.BToAL == 0);
        True(transfer.A.StorageL < highHead.StorageL && transfer.B.StorageL > lowHead.StorageL);
    }

    private static StepResult Advance(SimulationKernel kernel, BodyState state, double dt,
        EnvironmentSample? environment = null, StepInputs? inputs = null)
    {
        var result = kernel.Advance(state, environment ?? EnvironmentSample.Neutral, inputs ?? new StepInputs(), dt);
        True(result.Accepted, result.Error ?? "Step rejected");
        return result;
    }

    private static double HeatChange(BodyState before, BodyState after, BodyProfile profile) =>
        (after.CoreTemperatureC - before.CoreTemperatureC) * profile.CoreHeatCapacityJPerK
        + (after.SkinTemperatureC - before.SkinTemperatureC) * profile.SkinHeatCapacityJPerK;

    private static void Balanced(SimulationDiagnostics diagnostics)
    {
        Near(0, diagnostics.HeatBalanceResidualJ, 1e-5);
        Near(0, diagnostics.BodyWaterBalanceResidualL, 1e-9);
        Near(0, diagnostics.TotalWaterBalanceResidualL, 1e-9);
        Near(0, diagnostics.FuelBalanceResidualKJ, 1e-7);
    }

    private static void Rejected(StepResult result, BodyState prior)
    {
        True(!result.Accepted && result.Error is not null);
        True(ReferenceEquals(prior, result.State));
        Near(0, result.Diagnostics.AcceptedBioSeconds);
    }

    private static void True(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, double tolerance = 1e-12)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected:G17}, got {actual:G17}, tolerance {tolerance:G3}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
