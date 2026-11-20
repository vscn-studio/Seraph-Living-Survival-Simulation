using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class PollutionChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static void VegetationSurvey()
    {
        var heights = Enumerable.Repeat((ushort)110, 1024).ToArray();
        int reads = 0;
        EcologyCell Read(int x, int y, int z)
        {
            reads++;
            if (y == 100) return EcologyCell.Forest;
            if (y == 71) return EcologyCell.Vegetation;
            if (y == 70) return EcologyCell.Ground;
            if (y < 70) throw new Exception("Survey passed through solid terrain.");
            return EcologyCell.Open;
        }
        var batched = new SurfaceEcologySurvey();
        while (!batched.Complete)
        {
            var before = reads;
            var used = batched.Advance(heights, 200, 17, Read);
            Check(used <= 17 && used == reads - before, "A survey must yield within its read budget.");
        }
        var continuous = new SurfaceEcologySurvey();
        continuous.Advance(heights, 200, 10000, Read);
        Check(batched.ForestColumns == 64 && batched.VegetationColumns == 64 && batched.ObservedColumns == 64,
            "Tall crowns must not hide ground vegetation more than 16 blocks below the rain height.");
        Check(continuous.Complete && continuous.ForestColumns == batched.ForestColumns
            && continuous.VegetationColumns == batched.VegetationColumns, "Yielding must preserve coverage results.");
        var unloaded = new SurfaceEcologySurvey();
        unloaded.Advance(heights, 200, 100, (_, _, _) => EcologyCell.Unavailable);
        Check(unloaded.Complete && unloaded.ObservedColumns == 0, "Unloaded terrain must not become deforestation evidence.");
    }

    public static void FuelSignatures()
    {
        var wood = RegionalEnvironmentModel.Combustion(BurningFuel.Wood);
        var charcoal = RegionalEnvironmentModel.Combustion(BurningFuel.Charcoal);
        var coal = RegionalEnvironmentModel.Combustion(BurningFuel.Coal);
        Check(charcoal.Particles < wood.Particles && charcoal.CO > 0, "Charcoal is cleaner but still emits CO.");
        Check(coal.SO2 > wood.SO2 && coal.PM10 > 0, "Coal must supply sulfur and particulates.");
        var clean = new RegionalEnvironment();
        var smoke = RegionalEnvironmentModel.Regional(clean, new() { Emissions = wood }, 1d / 12, 0, 0);
        Check(smoke.Air.CO > 0 && smoke.AirbornePathogens == 0, "Smoke must not fabricate a biological source.");
    }

    public static void RunoffAndInfiltration()
    {
        var sources = new RegionalSources { Fecal = 6, Nitrogen = .2, Phosphorus = .1 };
        var wet = new RegionalEnvironment(); var dry = wet;
        for (var i = 0; i < 120; i++)
        {
            wet = RegionalEnvironmentModel.Regional(wet, sources, 1d / 12, 1, 0);
            dry = RegionalEnvironmentModel.Regional(dry, sources, 1d / 12, 0, 0);
        }
        Check(wet.Valid() && wet.SoilPathogens > 0 && wet.SurfacePathogens > 0 && wet.GroundPathogens > 0,
            "Fecal sources must reach soil, surface water and groundwater.");
        Check(wet.SurfaceNitrate == dry.SurfaceNitrate && wet.SurfacePhosphate == dry.SurfacePhosphate
            && wet.GroundNitrate == dry.GroundNitrate, "Disabled chemical transport must remain inert.");
        Check(wet.Air.NH3 > 0 && wet.Air.CH4 > 0 && wet.Air.H2S > 0, "Wet waste must emit organic gases.");
    }

    public static void AgentPersistence()
    {
        var source = new RegionalSources { Pathogens = new Dictionary<string, double> { ["giardia"] = .4 } };
        var transportInitial = new RegionalEnvironment { SoilAgents = new Dictionary<string, double> { ["giardia"] = .2 } };
        var full = RegionalEnvironmentModel.Regional(transportInitial, source, .25, .6, 0);
        var half = RegionalEnvironmentModel.Regional(transportInitial, source, .125, .6, 0);
        half = RegionalEnvironmentModel.Regional(half, source, .125, .6, 0);
        Check(Math.Abs(full.SurfaceAgents["giardia"] - half.SurfaceAgents["giardia"]) < 1e-12
            && Math.Abs(full.GroundAgents["giardia"] - half.GroundAgents["giardia"]) < 1e-12,
            "Coupled pathogen transport must be invariant to settlement partitioning.");
        static void Close(double a, double b) { if (Math.Abs(a - b) > 1e-10) throw new Exception($"{a} != {b}"); }
        var agents = new Dictionary<string, double> { ["giardia"] = .8 };
        var initial = new RegionalEnvironment { SurfaceAgents = agents, SoilAgents = agents, GroundAgents = agents };
        var after = initial;
        // No runoff into surface water; soil still infiltrates groundwater and is checked separately.
        for (var i = 0; i < 48; i++) after = RegionalEnvironmentModel.Regional(after, new(), .25, 0, 0);
        Close(after.SurfaceAgents["giardia"], .4);
        Close(after.SoilAgents["giardia"], .8 * Math.Pow(.5, .5));
        var ground = new RegionalEnvironment { GroundAgents = agents };
        for (var i = 0; i < 288; i++) ground = RegionalEnvironmentModel.Regional(ground, new(), .25, 0, 0);
        Close(ground.GroundAgents["giardia"], .4);
        if (ground.GroundAgents.ContainsKey("ecoli")) throw new Exception("Persistence invented a pathogen.");
        if (new RegionalEnvironment().CropGrowthMultiplier != 1 ||
            new RegionalEnvironment { HeavyMetals = 1, Acidity = 1, Erosion = 1, SoilNitrogen = 1, SoilPhosphorus = 1 }.CropGrowthMultiplier != 1)
            throw new Exception("Disabled environmental chemistry must not alter crop growth.");
    }

    public static void DispersionAndRecovery()
    {
        var dirty = new RegionalEnvironment { Air = new(.5, .5, .5, .4, .2, .1, .1), SurfacePathogens = .4,
            GroundPathogens = .2, SoilPathogens = .3, SoilNitrogen = .2, Acidity = .1 };
        var calm = RegionalEnvironmentModel.Regional(dirty, new(), .25, 0, 0);
        var weather = RegionalEnvironmentModel.Regional(dirty, new(), .25, 1, 2);
        Check(weather.Air.CO < calm.Air.CO && calm.Air.CO < dirty.Air.CO, "Wind and rain must disperse regional gas chemistry.");
        var downwind = RegionalEnvironmentModel.Regional(new(), new(), .25, 0, 2, dirty.Air);
        Check(downwind.Air.CO > 0, "A loaded upwind cell must affect the downwind cell.");
        var recovered = dirty;
        for (var i = 0; i < 4000; i++) recovered = RegionalEnvironmentModel.Regional(recovered, new(), .25, 0, 1);
        Check(recovered.SurfacePathogens < .001 && recovered.GroundPathogens < .001 && recovered.Air.Total < .001,
            "Removed sources must not leave permanent biological or air contamination.");
    }

    public static void SmokeCoupledGasTransport()
    {
        var dirty = new RegionalEnvironment { Air = new(.8, .6, .5, .7, .6, .4, .7) };
        var gases = new AirChemistry(.8, .6, .5, .7, .6, .4, .7);
        var unvented = RegionalEnvironmentModel.Regional(new() { Air = gases }, new(), .25, 0, 0);
        var vented = RegionalEnvironmentModel.Regional(new() { Air = gases },
            new() { ChimneyScrub = 1 }, .25, 0, 0);
        var planted = RegionalEnvironmentModel.Regional(new() { Air = gases },
            new() { Forest = 1, Vegetation = 1, ChimneyScrub = 1 }, .25, 0, 0);
        Check(vented.Air == unvented.Air && planted.Air == unvented.Air,
            "Regional pollution must not independently scrub gases outside the transported smoke plume.");
        Check(Math.Abs(gases.Toxicity - (.7 * .3 + .7 * .6 + .6 * .15)) < 1e-12,
            "CO must not be counted twice as both CO and toxic gas.");

        var air = EnvironmentSample.Neutral with { CarbonMonoxide = .8, ToxicGas = .6 };
        var absent = SmokeGasCoupling.Apply(air, false, default);
        var clean = SmokeGasCoupling.Apply(air, true, default);
        var plume = SmokeGasCoupling.Apply(air, true, new(.4, 0, 0, .3, 0, 0, 0));
        Check(absent.CarbonMonoxide == 0 && absent.ToxicGas == 0
            && clean.CarbonMonoxide == 0 && clean.ToxicGas == 0,
            "No or invalid RealSmoke sample must pause new gas exposure.");
        Check(plume.CarbonMonoxide == .4 && Math.Abs(plume.ToxicGas - .09) < 1e-12,
            "Gas exposure must use measured species, independent of regional readings.");
    }

    public static void EcologyAndExtraction()
    {
        var forest = new RegionalSources { Forest = .8, Vegetation = .8, Wildlife = 12 };
        var old = RegionalEnvironmentModel.Create(forest);
        var damaged = RegionalEnvironmentModel.World(old, forest with { Forest = .1, Vegetation = .05,
            Wildlife = 2, Grazing = 30, Cultivation = .8 }, 1, 5000);
        Check(damaged.ForestLoss > .8 && damaged.Erosion > 0
            && damaged.OrganicMatter < 1, "Land use must affect erosion and organic matter.");
        Check(damaged.GroundwaterStress > .4 && damaged.RechargeMultiplier < 1, "Overdraw must lower recharge efficiency.");
        var restored = damaged;
        for (var i = 0; i < 200; i++) restored = RegionalEnvironmentModel.World(restored, forest, 1, 0);
        Check(restored.GroundwaterStress < .01 && restored.ForestLoss == 0,
            "Actual regrowth and restrained extraction must allow recovery.");
        Check(restored.Valid(), "Ecology must remain bounded.");
    }

    public static void ExposureAndFunction()
    {
        var body = BodyState.Create(); var profile = BodyProfile.Default;
        var kernel = new FrameworkKernel(); var state = new FrameworkState();
        var toxicAir = EnvironmentSample.Neutral with { CarbonMonoxide = 1, ToxicGas = .8, AirPollution = .8 };
        Check(EnvironmentalExposure.RespiratoryHazard(toxicAir, 1) == 0, "Combustion cannot cause pathogen exposure.");
        var activity = new ActivityContext(0, false, false, false, false, 1, true);
        for (var i = 0; i < 120; i++)
        {
            var step = kernel.Advance(state, body, profile, toxicAir, activity, 8);
            Check(step.Accepted, step.Error ?? "Rejected toxic exposure."); state = step.State;
        }
        var impaired = BodyFunctionResolver.Evaluate(state, body, profile);
        var healthy = BodyFunctionResolver.Evaluate(new(), body, profile);
        Check(impaired.Endurance < healthy.Endurance && impaired.Consciousness < healthy.Consciousness,
            "CO must reduce actual functional capacity.");
        Check(ClinicalEffects.Evaluate(state, body, profile).Any(e => e.Id == "carbon-monoxide")
            && ClinicalEffects.DamagePerSecond(state, body, profile, true) > 0, "Severe CO must have symptoms and health damage.");
        var previous = state.CarbonMonoxideBurden;
        for (var i = 0; i < 120; i++) state = kernel.Advance(state, body, profile, EnvironmentSample.Neutral, activity, 8).State;
        Check(state.CarbonMonoxideBurden < previous * .1, "Clean air must clear CO burden.");
    }

    public static void ExposureAccounting()
    {
        var body = BodyState.Create();
        var profile = BodyProfile.Default;
        var kernel = new FrameworkKernel();
        var activity = new ActivityContext(0, false, false, false, false, 1, true);
        var state = FrameworkState.Create("pollution-accounting");
        var clean = kernel.Advance(state, body, profile, EnvironmentSample.Neutral, activity, 8);
        Check(clean.Accepted && clean.State.ChemicalBurden == 0, "No-source air must not create chemical burden.");
        var polluted = EnvironmentSample.Neutral with { AirPollution = .9, ToxicGas = .8, CarbonMonoxide = 0 };
        var exposed = kernel.Advance(state, body, profile, polluted, activity, 8);
        Check(exposed.Accepted && exposed.State.ChemicalBurden > 0,
            "Toxic ambient input must enter the chemical burden pathway.");
        Check(exposed.State.CarbonMonoxideBurden == 0, "Chemical gas must remain separate from CO burden.");
        Check(EnvironmentalExposure.ChemicalInput(polluted) == .8
            && EnvironmentalExposure.ChemicalInput(EnvironmentSample.Neutral) == 0,
            "Chemical input must use the named toxic-gas channel only.");
        var preview = EnvironmentalExposure.Preview(state, ExposureRoute.Ingestion,
            new Dictionary<string, double> { ["shigella"] = 25 });
        Check(preview.Available && preview.InputUnits == 25 && preview.RetainedUnits == 25
            && preview.OverflowUnits == 0 && preview.HazardEstimate == 0,
            "A below-capacity interaction should be retained by the barrier without immediate overflow hazard.");
    }

    public static void WaterDoseAndChemistry()
    {
        var sources = new PollutionLoad(5, 4, 1, 0, 0);
        var cold = PollutionModel.WaterHazard(sources, 0, false, false, .02, .8);
        var warm = PollutionModel.WaterHazard(sources, 25, false, false, .02, .8);
        var ground = PollutionModel.WaterHazard(sources, 25, true, false, .02, .8);
        var chemicals = new PollutionLoad(0, 0, 0, 0, 4);
        Check(PollutionModel.Levels(chemicals).Water == 0 && PollutionModel.Levels(chemicals).BiologicalAir == 0
            && PollutionModel.WaterHazard(chemicals, 25, false, false, .02, .8)
                == PollutionModel.WaterHazard(default, 25, false, false, .02, .8),
            "Chemical pollution must not fabricate enteric or respiratory pathogens.");
        Check(warm > cold && ground > 0 && ground < warm, "Temperature and soil filtration must affect risk without sterilizing water.");
        var one = PollutionModel.DoseRisk(warm, .25);
        var two = PollutionModel.DoseRisk(warm, .5);
        Check(Math.Abs(two - (1 - (1 - one) * (1 - one))) < 1e-12, "Serving risk must respect repeated independent doses.");
        var a = new WaterBatch { VolumeL = 1, ChemicalHazardPerL = .8 };
        var b = new WaterBatch { VolumeL = 3 };
        var mixed = a.Mix(b);
        Check(mixed.ChemicalHazardPerL == 0, "Disabled water chemistry must not survive mixing.");
        var contaminated = a with { PathogensPerL = new Dictionary<string, double> { ["shigella"] = .3 } };
        var diluted = contaminated.Mix(b);
        Check(diluted.PathogensPerL.Count == 1 && Math.Abs(diluted.PathogensPerL["shigella"] * diluted.VolumeL - .3) < 1e-12,
            "Water must preserve its actual agent load without inventing other diseases.");
    }

    public static void Bounds()
    {
        var old = new RegionalEnvironment();
        Check(ReferenceEquals(old, RegionalEnvironmentModel.Regional(old, new(), double.NaN, 0, 0)), "Invalid time must retain state.");
        Check(!(old with { GroundNitrate = double.NaN }).Valid(), "Nonfinite persisted states must be rejected.");
        var extreme = RegionalEnvironmentModel.Regional(old, new() { Fecal = 1e9, Nitrogen = 1e9,
            Phosphorus = 1e9, Metals = 1e9, Emissions = new(1e9, 1e9, 1e9, 1e9, 1e9, 1e9, 1e9) }, 10000, 1, 100);
        Check(extreme.Valid(), "Extreme finite source loads must remain bounded.");
        Check((EnvironmentSample.Neutral with { CarbonMonoxide = 1.01 }).Validate() is not null,
            "Invalid CO exposure must be rejected.");
    }
}

