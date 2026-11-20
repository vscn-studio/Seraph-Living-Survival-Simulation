using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Uses only measured transported species for physiological gas exposure.</summary>
public static class SmokeGasCoupling
{
    public static EnvironmentSample Apply(EnvironmentSample environment, bool sampleValid, AirChemistry gas)
    {
        // Missing and failed transport data are unknown, so they cannot create new gas dose.
        return environment with
        {
            CarbonMonoxide = sampleValid && double.IsFinite(gas.CO) ? Math.Clamp(gas.CO, 0, 1) : 0,
            ToxicGas = sampleValid && double.IsFinite(gas.Toxicity) ? Math.Clamp(gas.Toxicity, 0, 1) : 0
        };
    }
}
