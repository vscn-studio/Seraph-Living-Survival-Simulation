using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class ReleaseFormatChecks
{
    internal static void Run()
    {
        var save = new ShadowSave { WorldId = "w", PlayerUid = "p", Profile = BodyProfile.Default,
            State = BodyState.Create(), Framework = FrameworkState.Create("w:p") };
        var bytes = ShadowSaveCodec.Encode(save);
        if (!ShadowSaveCodec.Decode(bytes, "w", "p").Success) throw new Exception("v1 roundtrip failed");
        // Old saves lack the zinc marker and used slot 11 as a zeroed placeholder.
        var legacyEnvelope = JsonNode.Parse(bytes)!.AsObject();
        var legacyPayload = JsonNode.Parse(Convert.FromBase64String(legacyEnvelope["Payload"]!.GetValue<string>()))!.AsObject();
        var legacyDiet = legacyPayload["Framework"]!["Nutrition"]!.AsObject();
        legacyDiet.Remove("IndependentZinc"); legacyDiet["Reserve"]![11] = 0;
        legacyDiet["Reserve"]![10] = 7;
        var legacyRaw = JsonSerializer.SerializeToUtf8Bytes(legacyPayload);
        legacyEnvelope["Payload"] = Convert.ToBase64String(legacyRaw);
        legacyEnvelope["Sha256"] = Convert.ToHexString(SHA256.HashData(legacyRaw));
        var migrated = ShadowSaveCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(legacyEnvelope), "w", "p").Save!;
        if (migrated.Framework.Nutrition.Get("zinc").Reserve != 50 || migrated.Framework.Nutrition.Get("iron").Reserve != 7)
            throw new Exception("Zinc migration overwrote iron or started an artificial deficiency");
        migrated.Framework.Nutrition.Reserve[11] = 19;
        var restored = ShadowSaveCodec.Decode(ShadowSaveCodec.Encode(migrated), "w", "p").Save!;
        if (restored.Framework.Nutrition.Get("zinc").Reserve != 19) throw new Exception("Zinc migration repeated");
        void Reject(Action<JsonObject, JsonObject> change)
        {
            var envelope = JsonNode.Parse(bytes)!.AsObject();
            var payload = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!.AsObject();
            change(envelope, payload);
            var raw = JsonSerializer.SerializeToUtf8Bytes(payload);
            envelope["Payload"] = Convert.ToBase64String(raw);
            envelope["Sha256"] = Convert.ToHexString(SHA256.HashData(raw));
            var input = JsonSerializer.SerializeToUtf8Bytes(envelope); var original = (byte[])input.Clone();
            if (ShadowSaveCodec.Decode(input, "w", "p").Success || !input.SequenceEqual(original))
                throw new Exception("Rejected data loaded or modified");
        }
        Reject((e,p) => e["Magic"] = "SERAPH");
        Reject((e,p) => p.Remove("Format"));
        Reject((e,p) => p["Format"] = "rc");
        Reject((e,p) => p["SchemaVersion"] = 11);
        Reject((e,p) => p["Framework"]!["Nutrition"]!["Version"] = 5);
        Reject((e,p) => p["Framework"]!["Nutrition"]!["LegacyRepairProteinG"] = 1);
        Reject((e,p) => p["State"]!["GutWaterL"] = 100);
        Reject((e,p) => p["Framework"] = null);
        Reject((e,p) => p["Framework"]!.AsObject().Remove("Version"));
        Reject((e,p) => p["Framework"]!["Nutrition"]!.AsObject().Remove("Known"));
        Reject((e,p) => p["Framework"]!["Vitals"]!.AsObject().Remove("IntegrityVersion"));
        var config = new SeraphServerConfig();
        if (config.Validate() is not null || (config with { ConfigVersion = 13 }).Validate() is null)
            throw new Exception("Config version validation failed");
        try { JsonSerializer.Deserialize<SeraphServerConfig>("{\"ConfigVersion\":1}"); throw new Exception("Missing config identity accepted"); }
        catch (JsonException) { }
        var dose = new TreatmentDefinition { Id = "care", Group = "care", DelaySeconds = 60, DurationSeconds = 900 };
        if (dose.Validate() is not null || (dose with { TimeUnitVersion = 2 }).Validate() is null)
            throw new Exception("Treatment time contract failed");
    }
}
