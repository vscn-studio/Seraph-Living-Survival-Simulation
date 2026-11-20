using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Seraph.Server;

/// <summary>BloodTrail supplies its existing particles; Seraph remains the sole player loss ledger.</summary>
internal sealed class BleedingVisualBridge : IDisposable
{
    private readonly Harmony harmony;
    internal BleedingVisualBridge() : this(false) { }
    internal BleedingVisualBridge(bool client) : this(client, null) { }
    internal BleedingVisualBridge(bool client, ICoreAPI? api)
    {
        harmony = new(client ? "vsslssmod.bleedingvisuals.client" : "vsslssmod.bleedingvisuals");
        if (client)
        {
            var particles = AccessTools.TypeByName("BloodTrail.src.Client.EntityBleedingBehaviorParticles");
            if (particles is not null && AccessTools.DeclaredMethod(particles, "Initialize") is { } initialize)
            {
                try { harmony.Patch(initialize, postfix: new HarmonyMethod(typeof(BleedingVisualBridge), nameof(InitializeParticles))); }
                catch (Exception ex) { api?.Logger.Warning("[vsslssmod] BloodTrail particle hook failed: {0}", ex.Message); }
            }
            return;
        }
        // Optional reflection only: neither bleeding mod is required. Animals retain their own behavior.
        foreach (var (typeName, methods) in new[] {
            ("BloodTrail.src.Server.EntityBleedingBehavior", new[] { "OnEntityReceiveDamage", "StartBleeding", "GlobalBleedTick" }),
            ("ProgressiveBleeding.BleedingBehavior", new[] { "Begin", "OnGameTick" }) })
        {
            var type = AccessTools.TypeByName(typeName);
            if (type is null) continue;
            foreach (var name in methods)
                if (AccessTools.DeclaredMethod(type, name) is { } method)
                {
                    try
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(BleedingVisualBridge), nameof(AllowExternal)));
                    }
                    catch (Exception ex)
                    { api?.Logger.Warning("[vsslssmod] External bleeding hook {0}.{1} failed: {2}", typeName, name, ex.Message); }
                }
        }
    }

    private static bool AllowExternal(EntityBehavior __instance) => !Owns(__instance.entity);
    private static bool Owns(Entity entity) => entity is EntityPlayer && entity.World?.Side == EnumAppSide.Server
        && entity.Api?.ModLoader.GetModSystem<SeraphModSystem>()?.Server?.OwnsBleeding(entity) == true;

    internal static void AttachParticles(ICoreAPI api)
    {
        // The server's finalized entity definitions are sent to clients. Adding
        // only on client AssetsFinalize misses the authoritative player definition.
        if (!api.ModLoader.IsModEnabled("bloodtrail")) return;
        foreach (var player in api.World.EntityTypes.Where(p => p.Code?.ToString() == "game:player"))
        {
            try
            {
                var behaviors = player.Client.BehaviorsAsJsonObj ?? [];
                if (!behaviors.Any(b => b["code"]?.AsString() == "EntityBleedingBehaviorParticles"))
                    player.Client.BehaviorsAsJsonObj = [.. behaviors, JsonObject.FromJson("{\"code\":\"EntityBleedingBehaviorParticles\"}")];
            }
            catch { }
        }
    }

    private static void InitializeParticles(EntityBehavior __instance)
    {
        // A joining client can receive isBleeding before Initialize registers its
        // modified listener. Bootstrap the current state as well as future changes.
        if (__instance.entity.WatchedAttributes.GetBool("isBleeding"))
            AccessTools.Method(__instance.GetType(), "OnBleedingStateChanged")?.Invoke(__instance, null);
    }

    internal static void Sync(Entity entity, bool bleeding)
    {
        // Client particles watch this attribute. No external timer, random chance or damage is started.
        if (entity.WatchedAttributes.GetBool("isBleeding") == bleeding) return;
        entity.WatchedAttributes.SetBool("isBleeding", bleeding);
        entity.WatchedAttributes.MarkPathDirty("isBleeding");
    }
    public void Dispose() => harmony.UnpatchAll(harmony.Id);
}
