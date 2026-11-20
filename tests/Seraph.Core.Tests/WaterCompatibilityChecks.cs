using System.Text.Json;
using Seraph.Core.Framework;
internal static class WaterCompatibilityChecks
{
    internal static void ReleaseFormat()
    {
        var water = SurfaceWater.Apply(new() { VolumeL = 10 }, "pond", new());
        using var current = JsonDocument.Parse(JsonSerializer.Serialize(water));
        var copy = WaterQualityCompatibility.Read(current.RootElement)!;
        if (copy.VolumeL != 10 || copy.EntericHazardPerL != water.EntericHazardPerL || copy.PathogensPerL.Count != 0)
            throw new Exception("Water v1 roundtrip changed dose.");
        foreach (var json in new[] { "null", "{}", "{\"QualityVersion\":1}", "{\"Format\":\"seraph.release.water\",\"QualityVersion\":2}" })
        {
            try { using var doc = JsonDocument.Parse(json); WaterQualityCompatibility.Read(doc.RootElement); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { continue; }
            throw new Exception("Legacy water accepted.");
        }
    }
}
