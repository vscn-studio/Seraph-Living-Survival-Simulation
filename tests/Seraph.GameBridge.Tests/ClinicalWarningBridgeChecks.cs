using System.Reflection;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

internal static class ClinicalWarningBridgeChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, null!);
        player.WorldData = (IWorldPlayerData)RoutingProxy.Make(typeof(IWorldPlayerData), (m, _) =>
            m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        long clock = 1000;
        var world = (IServerWorldAccessor)RoutingProxy.Make(typeof(IServerWorldAccessor), (m, _) =>
            m.Name == "get_ElapsedMilliseconds" ? clock : throw new Exception(m.Name));
        var api = (ICoreServerAPI)RoutingProxy.Make(typeof(ICoreServerAPI), (m, _) =>
            m.Name == "get_World" ? world : throw new Exception(m.Name));
        var type = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(type, [api])!;
        type.GetField("settings", all)!.SetValue(server, new SettingsAuthority(new() { Mode = "survival" }));
        var sessionType = type.GetNestedType("Session", all)!;
        var session = Activator.CreateInstance(sessionType, all, null, [player], null)!;
        void Set(string key, object value) => sessionType.GetProperty(key)!.SetValue(session, value);
        var tracker = (ClinicalWarningTracker)sessionType.GetProperty("ClinicalWarnings")!.GetValue(session)!;
        void Announce() => type.GetMethod("AnnounceClinicalEffects", all)!.Invoke(server, [session]);
        Set("Environment", EnvironmentSample.Neutral); Set("LastGoodSampleSeconds", 1d); Set("LastDt", .5d);
        tracker.Seed([]);
        Set("PendingInsectBites", 2);
        Announce();
        Check(player.IngameErrors.Count == 1 && player.SentMessages.Count == 1
            && player.IngameErrors[0].Code == "vsslssmod-clinical", "Bite did not reach native red error and chat APIs.");
        Announce(); Check(player.IngameErrors.Count == 1, "Bite replayed.");
        var frame = BloodCirculation.AddWaste(FrameworkState.Create(player.PlayerUID), BodyState.Create(), BodyProfile.Default, 1100);
        Set("Framework", frame); Announce();
        Check(player.IngameErrors.Count == 2 && player.SentMessages.Count == 2, "Severe blood warning was throttled.");
        Announce(); Check(player.IngameErrors.Count == 2, "Unchanged blood warning repeated.");
        type.GetField("paused", all)!.SetValue(server, true);
        Set("PendingInsectBites", 1); Announce();
        Check(player.IngameErrors.Count == 2, "Paused warning clock advanced.");
        type.GetField("paused", all)!.SetValue(server, false);
        clock = 8000; Announce(); Check(!tracker.Initialized, "Stale environment retained episode state.");
        Set("LastGoodSampleSeconds", 8d); Announce();
        Check(player.IngameErrors.Count == 2, "Stale bite or relog baseline replayed.");
        entity.Alive = false; Announce(); Check(!tracker.Initialized, "Death retained episode state.");
        var wash = type.GetMethod("WashActionKey", all)!;
        foreach (var (kind, bath, expected) in new[] { ("clean-body", false, "clean-body"), ("clean-wound", false, "clean-wound"),
            ("clean", false, "clean"), ("clean-body", true, "bathe"), ("clean", true, "bathe-wound"), ("clean-wound", true, "bathe-wound") })
            Check((string)wash.Invoke(null, [kind, bath])! == expected, "Wash context lost.");
        Console.WriteLine("PASS native red warning/chat delivery, bite receipt, severe bypass, no replay, pause/stale/death cleanup and wash context");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
