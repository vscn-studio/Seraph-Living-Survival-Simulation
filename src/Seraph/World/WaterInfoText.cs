using System.Globalization;
using Seraph.Core.Framework;

namespace Seraph.World;

/// <summary>Shared presentation for authoritative source samples and synchronized container water.</summary>
internal static class WaterInfoText
{
    internal static string[] Lines(WaterBatch? water, System.Func<string, object[], string> text, double servingL = .25, bool includeVolume = true, bool includeWaste = true)
    {
        string L(string key, params object[] args) => text(key, args);
        string N(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";
        var worldVolume = water is { VolumeL: 1000, Source: "surface" or "salt" or "fecal" or "polluted" or "groundwater" or "pond" or "lake" or "river" };
        // Source labels already describe fecal/polluted/mixed/unverified water.
        // Do not repeat that same record on a separate line.
        var source = water is null ? "" : L("water-source-" + water.Source);
        if (water?.OriginSource is { } origin && origin != water.Source) source += " / " + L("water-source-" + origin);
        var lines = new List<string> { L("water-info", source) };
        if (includeVolume && !worldVolume) lines.Add(L("water-volume", N(water?.VolumeL, "0.##")));
        lines.AddRange([L("water-temperature", N(water?.TemperatureC, "0.#")),
            L("water-salt", N(water?.SaltGramsPerL, "0.##")),
            L("water-turbidity", N(water?.Turbidity, "P0"))]);
        return water is null ? lines.ToArray() : lines.Concat(PollutionText.Water(water, text, servingL, includeWaste)).ToArray();
    }
}
