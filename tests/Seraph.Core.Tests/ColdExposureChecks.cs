using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class ColdExposureChecks
{
    public static void RegionalFrostbite()
    {
        var kernel = new FrameworkKernel();
        // Regional frostbite must follow the exposed local environment even when mean skin temperature remains warm.
        var body = BodyState.Create() with { SkinTemperatureC = 20 };
        var uncovered = EnvironmentSample.Neutral with
        {
            AirTemperatureC = -15,
            DampSkin = true,
            ColdExposureByRegion = new Dictionary<string, double> { ["left-foot"] = 1 }
        };
        var state = FrameworkState.Create("cold-test");
        for (var i = 0; i < 700 && state.Wounds.Length == 0; i++)
            state = ColdExposure.Advance(kernel, state, body, BodyProfile.Default, uncovered, 8);
        if (!state.Wounds.Any(w => w.Kind == WoundKind.Frostbite && w.Region == "left-foot"))
            throw new Exception("Sustained severe exposure failed to create a regional frostbite wound.");

        var protectedSample = uncovered with { ColdExposureByRegion = new Dictionary<string, double> { ["left-foot"] = .2 } };
        var protectedState = FrameworkState.Create("protected-cold-test");
        for (var i = 0; i < 700; i++)
            protectedState = ColdExposure.Advance(kernel, protectedState, body, BodyProfile.Default, protectedSample, 8);
        if (protectedState.Wounds.Any(w => w.Kind == WoundKind.Frostbite))
            throw new Exception("Covered skin accumulated frostbite too quickly.");

        if (DiseaseSyndromes.Kit("common-cold") != SymptomKit.CommonCold
            || !Pathogens.Airborne("common-cold") || Pathogens.Airborne("respiratory") is false)
            throw new Exception("Common cold was merged with fever or lost airborne transmission.");
    }
}
