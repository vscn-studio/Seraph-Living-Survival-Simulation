using System.Security.Cryptography;
using System.Text.Json;
using Seraph.Core.State;
using Seraph.Core.Framework;

namespace Seraph.Application;

public sealed record ShadowSave
{
    [System.Text.Json.Serialization.JsonRequired]
    public string Format { get; init; } = "seraph.release.player";
    [System.Text.Json.Serialization.JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [System.Text.Json.Serialization.JsonRequired]
    public int ModelVersion { get; init; } = 1;
    public required string WorldId { get; init; }
    public required string PlayerUid { get; init; }
    public long LifeGeneration { get; init; }
    public long CheckpointSequence { get; init; }
    public bool Deceased { get; init; }
    public DateTime SavedAtUtc { get; init; }
    public required BodyProfile Profile { get; init; }
    public required BodyState State { get; init; }
    // Current state is always present; missing state is not a new life.
    [System.Text.Json.Serialization.JsonRequired]
    public FrameworkState Framework { get; init; } = FrameworkState.Create("new-life");
    [System.Text.Json.Serialization.JsonRequired]
    public InteractionState Interactions { get; init; } = new();
    public string PackageVersion { get; init; } = Seraph.Core.BuildInfo.Version;
}

public sealed record SaveDecodeResult(ShadowSave? Save, string? Error)
{
    public bool Success => Save is not null && Error is null;
}

public static class ShadowSaveCodec
{
    public const int MaxBytes = 128 * 1024;
    private sealed record Envelope(string Magic, int SchemaVersion, byte[] Payload, string Sha256);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static byte[] Encode(ShadowSave save)
    {
        var error = Validate(save, save.WorldId, save.PlayerUid);
        if (error is not null) throw new InvalidDataException(error);
        var payload = JsonSerializer.SerializeToUtf8Bytes(save, Json);
        var result = JsonSerializer.SerializeToUtf8Bytes(new Envelope("SERAPH-RELEASE", save.SchemaVersion, payload,
            Convert.ToHexString(SHA256.HashData(payload))), Json);
        if (result.Length > MaxBytes) throw new InvalidDataException("Save exceeds 128 KiB.");
        return result;
    }

    public static SaveDecodeResult Decode(byte[] bytes, string worldId, string playerUid)
    {
        if (bytes.Length is 0 or > MaxBytes) return new(null, "Invalid save length.");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json);
            if (envelope is null || envelope.Magic != "SERAPH-RELEASE" || envelope.SchemaVersion != 1)
                return new(null, "Unknown save envelope/schema.");
            if (envelope.Payload is null || envelope.Payload.Length > MaxBytes ||
                envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
                return new(null, "Save checksum mismatch.");
            var save = JsonSerializer.Deserialize<ShadowSave>(envelope.Payload, Json);
            if (save is not null && save.SchemaVersion != envelope.SchemaVersion)
                return new(null, "Save envelope/payload version mismatch.");
            var error = save is null ? "Empty save payload." : Validate(save, worldId, playerUid);
            if (error is null && save is not null)
            {
                save = MigrateNutrition(save);
                error = Validate(save, worldId, playerUid);
            }
            if (error is null && save is not null && !save.Framework.Nutrition.IndependentZinc)
            {
                var diet = save.Framework.Nutrition;
                var reserve = (double[])diet.Reserve.Clone();
                var pending = (double[])diet.Pending.Clone();
                var known = (bool[])diet.Known.Clone();
                var burden = (double[])diet.DeficiencyBurden.Clone();
                var zinc = Nutrients.IndexOf("zinc");
                reserve[zinc] = 50; pending[zinc] = 0; known[zinc] = true; burden[zinc] = 0;
                save = save with { Framework = save.Framework with { Nutrition = diet with {
                    IndependentZinc = true, Reserve = reserve, Pending = pending, Known = known, DeficiencyBurden = burden } } };
            }
            return error is null ? new(save, null) : new(null, error);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return new(null, "Malformed save: " + ex.Message);
        }
    }

    private static ShadowSave MigrateNutrition(ShadowSave save)
    {
        var diet = save.Framework.Nutrition;
        if (diet.CalibrationVersion >= 2) return save;
        var reserve = (double[])diet.Reserve.Clone();
        var pending = (double[])diet.Pending.Clone();
        var electrolyte = Nutrients.IndexOf("electrolytes");
        // Legacy electrolytes used a 0..100 gram-like scale. Preserve its ratio
        // while moving to the physical 10.5 g capacity.
        reserve[electrolyte] = Math.Clamp(reserve[electrolyte] * Nutrients.All[electrolyte].Maximum / 100, 0, Nutrients.All[electrolyte].Maximum);
        pending[electrolyte] = Math.Max(0, pending[electrolyte] * Nutrients.All[electrolyte].Maximum / 100);
        foreach (var id in new[] { "a", "c", "bcomplex" })
        {
            var i = Nutrients.IndexOf(id);
            pending[i] = Math.Max(0, pending[i] * MicroNutrition.DailyPointsFor(id) / MicroNutrition.DailyPoints);
        }
        return save with { Framework = save.Framework with { Nutrition = diet with {
            CalibrationVersion = 2, Reserve = reserve, Pending = pending } } };
    }

    private static string? Validate(ShadowSave save, string worldId, string playerUid)
    {
        if (save.Format != "seraph.release.player" || save.SchemaVersion != 1 || save.ModelVersion != 1)
            return "Unsupported release state/model format version.";
        if (string.IsNullOrWhiteSpace(save.WorldId) || string.IsNullOrWhiteSpace(save.PlayerUid)
            || save.WorldId != worldId || save.PlayerUid != playerUid) return "Save identity mismatch.";
        if (save.LifeGeneration < 0 || save.CheckpointSequence < 0) return "Invalid save sequence.";
        if (save.Profile is null || save.State is null || save.Framework is null || save.Interactions is null) return "Missing profile/state.";
        return save.State.Validate(save.Profile) ?? save.Framework?.Validate() ?? save.Interactions?.Validate();
    }
}
