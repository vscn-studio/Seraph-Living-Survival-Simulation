using System.Collections;
using System.Reflection;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

internal static class SleepEyeCombatChecks
{
    private sealed class ZeroRandom : Random { public override double NextDouble() => 0; }
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly mod)
    {
        void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
        foreach (var pressure in new[] { .9, .95, 1d })
        {
            var serverEyes = new SleepEyeCycle(); var clientEyes = new SleepEyeCycle();
            var seenClosed = false; var reopened = false;
            for (var tick = 0; tick < 1200; tick++)
            {
                serverEyes.Advance(.25, pressure);
                if (tick % 4 == 0)
                {
                    var state = serverEyes.Capture();
                    var packet = new SleepEyeSnapshot { OpenTime = state.OpenTime, Phase = state.Phase,
                        Hold = state.Hold, Closing = state.Closing, Sequence = state.Sequence };
                    using var stream = new MemoryStream(); ProtoBuf.Serializer.Serialize(stream, packet); stream.Position = 0;
                    packet = ProtoBuf.Serializer.Deserialize<SleepEyeSnapshot>(stream);
                    Require(clientEyes.Restore(new(packet.OpenTime, packet.Phase, packet.Hold, packet.Closing, packet.Sequence)), "Server eye phase rejected after wire roundtrip.");
                }
                else for (var f = 0; f < 15; f++) clientEyes.Advance(1d / 60, pressure);
                Require(Math.Abs(serverEyes.Closure - clientEyes.Closure) < .015, "Server combat and client eye phase diverged.");
                seenClosed |= serverEyes.Closure == 1;
                reopened |= seenClosed && serverEyes.Closure == 0;
            }
            Require(seenClosed && reopened, "Missing server close/reopen cycle.");
            var before = clientEyes.Capture(); clientEyes.Advance(0, pressure);
            Require(before == clientEyes.Capture(), "Paused eye timer advanced.");
            Require(!clientEyes.Restore(new(double.NaN, 0, 0, false, 0)), "Malformed eye phase accepted.");
        }
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, null!);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        entity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        var random = new ZeroRandom();
        long milliseconds = 0;
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Rand" => random, "PlayerByUid" => player, "get_Side" => EnumAppSide.Server,
            "get_ElapsedMilliseconds" => milliseconds, _ => throw new Exception(m.Name)
        });
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name == "get_World" ? world : throw new Exception(m.Name));
        entity.World = world; entity.Api = api;
        var type = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var coordinator = Activator.CreateInstance(type, [api])!;
        var sessionType = type.GetNestedType("Session", All)!;
        var session = Activator.CreateInstance(sessionType, All, null, [player], null)!;
        ((IDictionary)type.GetField("sessions", All)!.GetValue(coordinator)!).Add(player.PlayerUID, session);
        sessionType.GetProperty("State")!.SetValue(session, BodyState.Create() with { SleepPressure = 1 });
        sessionType.GetProperty("Framework")!.SetValue(session, FrameworkState.Create(player.PlayerUID) with { AdrenalineLevel = 1, AdrenalineRemainingSeconds = 20 });
        var eyes = (SleepEyeCycle)sessionType.GetProperty("SleepEyes")!.GetValue(session)!;
        var attacker = new EntityPlayer();
        float Hit(double z)
        {
            attacker.Pos.SetPos(0, 0, z);
            object?[] args = [entity, new DamageSource { Type = EnumDamageType.BluntAttack, Source = EnumDamageSource.Entity, SourceEntity = attacker }, 10f];
            type.GetMethod("ApplyCombatProtection", All)!.Invoke(coordinator, args); return (float)args[2]!;
        }
        Require(Hit(1) == 0 && Hit(-1) == 0, "Awake player lost normal directional dodge at high sleep pressure.");
        var advance = type.GetMethod("AdvanceSleepEyes", All)!;
        for (var i = 0; i < 103; i++) { milliseconds += 250; advance.Invoke(coordinator, [session, true, .25d]); }
        Require(eyes.Closure == 1 && Hit(1) == 10 && Hit(-1) == 10, "Facing/adrenaline restored dodge during closed eyes.");
        for (var i = 0; i < 8; i++) { milliseconds += 250; advance.Invoke(coordinator, [session, true, .25d]); }
        Require(eyes.Closure == 0 && Hit(1) == 0, "Dodge did not return on reopening.");
        var rest = sessionType.GetProperty("ClosedEyeRest")!;
        rest.SetValue(session, Enum.ToObject(rest.PropertyType, 1));
        Require(Hit(1) == 10 && Hit(-1) == 10, "Voluntary closed-eye rest retained dodge.");
        rest.SetValue(session, Enum.ToObject(rest.PropertyType, 0));
        var tired = new TreeAttribute(); tired.SetInt("isSleeping", 1); entity.WatchedAttributes.SetAttribute("tiredness", tired);
        Require(Hit(1) == 10 && Hit(-1) == 10, "Bed sleep retained dodge.");
        tired.SetInt("isSleeping", 0); Require(Hit(1) == 0, "Waking retained closed-eye penalty.");
        eyes.Restore(new(0, .6, .7, true, 500));
        sessionType.GetProperty("SleepEyesUpdatedAt")!.SetValue(session, 0d);
        milliseconds = 50; Require(Hit(1) == 0, "Partial eyelid closure prematurely disabled dodge.");
        milliseconds = 200; Require(Hit(1) == 10 && eyes.Capture().Phase == .6, "Between-tick hit missed closure or advanced the timer twice.");
        advance.Invoke(coordinator, [session, false, .25d]);
        Require(eyes.Closure == 0 && eyes.Capture().OpenTime == 0, "Inactive physiology retained an involuntary blink.");
        Console.WriteLine("PASS server/client eye phase and wire sync; actual combat zero dodge while involuntarily closed/K resting/asleep, all facings/adrenaline, reopening and high-pressure normal dodge");
    }
}
