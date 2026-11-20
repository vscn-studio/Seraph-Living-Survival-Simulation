using Vintagestory.API.Common.Entities;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    internal void ProjectColdVisuals(Entity entity)
    {
        if (!OwnsThermal(entity)) return;
        var session = SessionFor(entity);
        var strength = NativeColdVisuals.Strength(session.State.CoreTemperatureC,
            settings.Config.EnableSymptomVisuals && entity.Alive && !session.Deceased);
        // Native body-temperature updates run independently. Project after them so the
        // stock frost vignette and first-person shivering follow our authoritative body.
        // Leave native temperature, environment sampling and damage ownership unchanged.
        if (entity.WatchedAttributes.GetFloat("freezingEffectStrength") != strength)
            entity.WatchedAttributes.SetFloat("freezingEffectStrength", strength);
    }
}

internal static class NativeColdVisuals
{
    // VS 1.22.7: clamp((37 - core) / 4 - .5, 0, 1). The native perception
    // effect owns smoothing, first-person shivering and camera-shake preferences.
    internal static float Strength(double coreTemperatureC, bool enabled)
        => enabled && double.IsFinite(coreTemperatureC)
            ? (float)Math.Clamp((35 - coreTemperatureC) / 4, 0, 1) : 0;
}
