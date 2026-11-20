using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>Read-only, server-authoritative values; drawn after the eyelids, never advances recovery.</summary>
internal static class ClosedEyeRestDisplay
{
    internal const int Width = 340, Height = 168;
    internal static double?[] Values(ServerSnapshot state) => new[] { "stamina", "fatigue", "sleeppressure" }
        .Select(key => state.StatusMetrics.FirstOrDefault(m => m.Key == key && m.Available))
        .Select(m => m is not null && double.IsFinite(m.Value) ? (double?)Math.Clamp(m.Value, 0, 100) : null).ToArray();

    internal static NativeProgressDisplay Create(ICoreClientAPI api) => new(api, "vsslssmod-rest-bars", 3, Width, Height,
        [[.25, .78, .47], [.95, .63, .22], [.65, .48, .9]]) { TrendKeys = ["stamina", "fatigue", "sleeppressure"] };
}
