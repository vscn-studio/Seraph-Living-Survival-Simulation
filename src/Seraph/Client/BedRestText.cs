using Seraph.Network;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.Client;

internal static class BedRestText
{
    internal static string Describe(Block bed, ServerSnapshot? snapshot)
    {
        if (snapshot?.BedRest is not { } rest) return Lang.Get("vsslssmod:bed-rest-waiting");
        var efficiency = Math.Clamp(BedRestProperties.Quality(bed) * rest.EnvironmentEfficiency + rest.TreatmentSupport, 0, 1);
        var pressureRate = efficiency * rest.PressureRecoveryPerGameHour;
        var fatigueRate = efficiency * rest.FatigueRecoveryPerGameHour;
        if (!double.IsFinite(pressureRate) || pressureRate <= 0 || !double.IsFinite(fatigueRate) || fatigueRate < 0)
            return Lang.Get("vsslssmod:bed-rest-waiting");
        return Lang.Get("vsslssmod:bed-rest-rates", 100 * pressureRate, 100 * fatigueRate);
    }
}
