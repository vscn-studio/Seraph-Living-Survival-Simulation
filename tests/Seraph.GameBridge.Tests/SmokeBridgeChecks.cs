using System.Collections;
using System.Reflection;
using Seraph;
using Seraph.Application;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

internal static class SmokeBridgeChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        void Require(bool c, string why) { if (!c) throw new Exception(why); }
        var mod = typeof(SeraphModSystem).Assembly;
        var adapterType = mod.GetType("Seraph.Server.MeasuredSmokeAdapter", true)!;
        var source = new RealSmoke.ModSystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "get_Systems" ? new ModSystem[] { source } : null);
        long now = 0;
        var entity = new EntityPlayer { Alive = true }; entity.Pos.SetPos(12, 60, 34);
        entity.WatchedAttributes.SetString("playerUID", "fixture-player");
        entity.LocalEyePos.Set(0, 1.6, 0);
        var player = FixturePlayer.Make(entity, Proxy<IPlayerInventoryManager>((_, _) => null));
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : null);
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch { "get_ElapsedMilliseconds" => now, "PlayerByUid" => player, _ => null });
        entity.World = world;
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch { "get_ModLoader" => loader, "get_World" => world, "get_Logger" => logger, _ => null });
        entity.Api = api;
        using var adapter = (IDisposable)Activator.CreateInstance(adapterType, flags, null, [api], null)!;
        object? Call(string name, params object?[] values) => adapterType.GetMethod(name, flags)!.Invoke(adapter, values);
        var args = new object?[] { entity, 0d };
        Require((bool)Call("TrySample", args)! && (double)args[1]! == 1, "Relative smoke amount adapter failed");
        Require(Math.Abs(source.SmokeController.LastCenter.Y - 61.6) < 1e-8, "Smoke sampled at feet instead of eyes");
        Require(entity.Pos.Y == 60, "Eye query mutated native position");
        Call("Ownership", entity, true);
        var scent = new SmokeScent.SmokeScentMod { Entity = entity };
        scent.TickFirepitSmoke(.5f); Require(scent.Drains == 0, "Measured firepit smoke paid saturation twice");
        scent.TickFoulFumes(entity); Require(scent.Drains == 1, "Unrelated fumes drain was suppressed");
        now = 501; scent.TickFirepitSmoke(.5f); Require(scent.Drains == 2, "Expired ownership suppressed fallback");
        Call("Ownership", entity, true); scent.ThrowInFirepit = true;
        try { scent.TickFirepitSmoke(.5f); } catch (InvalidOperationException) { }
        scent.TickFoulFumes(entity); Require(scent.Drains == 3, "Firepit exception leaked scoped suppression");
        scent.ThrowInFirepit = false;
        entity.Pos.Dimension = 1;
        Require(!(bool)Call("TrySample", args)!, "Unsupported smoke dimension accepted");
        scent.TickFirepitSmoke(.5f); Require(scent.Drains == 4, "Unsupported dimension lost native penalty");
        entity.Pos.Dimension = 0;
        var serverType = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(serverType, [api])!;
        var sessionType = serverType.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        ((IDictionary)serverType.GetField("sessions", flags)!.GetValue(server)!).Add(player.PlayerUID, session);
        serverType.GetField("smoke", flags)!.SetValue(server, adapter);
        serverType.GetField("capabilities", flags)!.SetValue(server, CapabilityRegistry.Create(new(), []));
        var tick = serverType.GetMethod("TickSmoke", flags)!;
        FrameworkState Frame() => (FrameworkState)sessionType.GetProperty("Framework")!.GetValue(session)!;
        for (var i = 0; i < 240; i++) { now += 250; tick.Invoke(server, [session, true, .25d]); }
        Require(Math.Abs(Frame().SmokeDose - 60) < 1e-8, "Dose charged calendar time or missed actual active seconds");
        for (var i = 0; i < 10; i++) tick.Invoke(server, [session, false, .25d]);
        Require(Frame().SmokeDose == 60, "Inactive physiology accumulated dose");
        source.SmokeController.Amount = 0;
        for (var i = 0; i < 720; i++) { now += 250; tick.Invoke(server, [session, true, .25d]); }
        Require(Math.Abs(Frame().SmokeDose - 30) < 1e-7, "Clean-air half-life diverged in coordinator");
        var settle = serverType.GetMethod("SettleSmokeInjury", flags)!;
        var pendingDamage = sessionType.GetField("SmokeDamageThisTick", flags)!;
        var clinicalDamage = sessionType.GetField("ClinicalDamageThisTick", flags)!;
        var health = Frame().Vitals.Health;
        pendingDamage.SetValue(session, .25d); clinicalDamage.SetValue(session, .1d);
        settle.Invoke(server, [session]);
        Require(Math.Abs(Frame().Vitals.Health - (health - .15)) < 1e-8, "Smoke duplicated accepted clinical injury");
        settle.Invoke(server, [session]);
        Require(Math.Abs(Frame().Vitals.Health - (health - .15)) < 1e-8, "Smoke injury replayed");
        pendingDamage.SetValue(session, .25d); clinicalDamage.SetValue(session, 1d);
        settle.Invoke(server, [session]);
        Require(Math.Abs(Frame().Vitals.Health - (health - .15)) < 1e-8, "Accelerated calendar amplified smoke injury");
        sessionType.GetProperty("Framework")!.SetValue(session, Frame() with { Downed = new() { Active = true } });
        pendingDamage.SetValue(session, .25d); clinicalDamage.SetValue(session, 0d);
        settle.Invoke(server, [session]);
        Require(Math.Abs(Frame().Vitals.Health - (health - .15)) < 1e-8, "Smoke bypassed external downed floor");
        source.SmokeController.Fail = true;
        tick.Invoke(server, [session, true, .25d]);
        Require(!(bool)adapterType.GetProperty("Ready", flags)!.GetValue(adapter)! && Math.Abs(Frame().SmokeDose - 30) < 1e-7,
            "Failed source charged dose or invented clean air");
        scent.TickFirepitSmoke(.5f); Require(scent.Drains == 5, "Adapter failure removed fallback penalty");
        Console.WriteLine("PASS measured smoke eyes/relative units, real-time dose, clean-air half-life, accepted injury accounting/downed/replay, stale/dimension/fault fallback and scoped external drain ownership");
    }
}

namespace RealSmoke
{
    public sealed class ModSystem : Vintagestory.API.Common.ModSystem
    {
        public TestController SmokeController { get; } = new();
    }
    public sealed class TestController
    {
        public int Amount = 8;
        public bool Fail;
        public Vec3d LastCenter = new();
        public int GetSmokeAmount<T>(in T region) where T : struct
        {
            if (Fail) throw new InvalidOperationException();
            LastCenter = ((Smoke.Simulation.SmokeQueryCylinder)(object)region).Center;
            return Amount;
        }
    }
}
namespace RealSmoke.Smoke.Simulation
{
    public readonly struct SmokeQueryCylinder
    {
        public readonly Vec3d Center;
        public SmokeQueryCylinder(Vec3d center, double horizontalRange, double verticalRange)
        {
            if (horizontalRange != .5 || verticalRange != .5) throw new Exception("Unexpected smoke query volume");
            Center = center;
        }
    }
}
namespace SmokeScent
{
    public sealed class SmokeScentMod
    {
        public int Drains;
        public bool ThrowInFirepit;
        public EntityPlayer Entity = null!;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void TickFirepitSmoke(float dt)
        { if (ThrowInFirepit) throw new InvalidOperationException(); DrainSaturation(Entity, dt); }
        public void TickFoulFumes(EntityPlayer entity) => DrainSaturation(entity, 1);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DrainSaturation(EntityPlayer entity, float amount) => Current!.Drains++;
        private static SmokeScentMod? Current;
        public SmokeScentMod() => Current = this;
    }
}
