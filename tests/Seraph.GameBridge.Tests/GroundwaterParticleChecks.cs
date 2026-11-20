using System.Reflection;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

internal static class GroundwaterParticleChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var player = FixtureClientPlayer.Make(new EntityPlayer());
        player.Entity.Pos.SetPos(10.5, 20, 30.5);
        var water = new Block { Code = new("game:water-still-7") };
        var now = 1000L;
        Action<float>? tick = null;
        var unregistered = false;
        var emitted = new List<SimpleParticleProperties>();
        var events = Proxy<IClientEventAPI>((m, a) => m.Name switch
        {
            "RegisterGameTickListener" => Register((Action<float>)a![0]!, (int)a[1]!),
            "UnregisterGameTickListener" => Unregister((long)a![0]!),
            _ => throw new Exception("Unexpected groundwater event " + m.Name)
        });
        long Register(Action<float> callback, int interval)
        {
            Require(interval == 250, "Groundwater polling interval changed.");
            tick = callback;
            return 7;
        }
        object? Unregister(long id)
        {
            Require(id == 7, "Groundwater removed another listener.");
            unregistered = true;
            return null;
        }
        var world = Proxy<IClientWorldAccessor>((m, a) => m.Name switch
        {
            "GetBlock" => water,
            "get_Player" => player,
            "get_ElapsedMilliseconds" => now,
            "SpawnParticles" => Capture((SimpleParticleProperties)a![0]!),
            _ => throw new Exception("Unexpected groundwater world " + m.Name)
        });
        object? Capture(SimpleParticleProperties particles) { emitted.Add(particles); return null; }
        var api = Proxy<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Event" => events,
            _ => throw new Exception("Unexpected groundwater API " + m.Name)
        });
        var type = mod.GetType("Seraph.Client.GroundwaterParticleRenderer", true)!;
        var renderer = (IDisposable)Activator.CreateInstance(type, hidden, null, [api], null)!;
        void Reveal(GroundwaterReveal packet) => type.GetMethod("Reveal", hidden)!.Invoke(renderer, [packet]);
        void Clear() { type.GetMethod("Clear", hidden)!.Invoke(renderer, null); emitted.Clear(); }
        GroundwaterReveal Packet(int x = 10, int duration = 15000) => new()
        {
            X = x, Y = 20, Z = 30, Dimension = 0, SurfaceY = 20, Radius = 8,
            DurationMs = duration, ExcavationExposure = true, SaturatedFaceMask = 15, WaterContent = 1
        };
        try
        {
            Reveal(Packet());
            tick!(.25f);
            Require(emitted.Count == 4 && emitted.Sum(p => p.MinQuantity + p.AddQuantity) == 20,
                "Four saturated faces must emit at most twenty particles per reveal.");
            var native = (SimpleParticleProperties)typeof(BlockBehaviorRainDrip)
                .GetField("dripParticle", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            foreach (var p in emitted)
            {
                Require(p.MinSize == native.MinSize && p.MaxSize == native.MaxSize,
                    "Groundwater droplets do not match native rain-drip size.");
                Require(p.ParticleModel == EnumParticleModel.Cube && p.MinVelocity.Y < 0
                    && p.MinVelocity.Y + p.AddVelocity.Y < 0 && p.GravityEffect > 0,
                    "Groundwater must fall as volume particles instead of crossed quads.");
                Require(ReferenceEquals(p.ColorByBlock, water) && p.WithTerrainCollision && p.ShouldDieInLiquid,
                    "Native water tint or collision/lifetime safeguards changed.");
                Require(p.MinPos.Y == 20.06 && Math.Abs(p.AddPos.Y - .88) < 1e-9
                    && (p.AddPos.X < .051 || p.AddPos.Z < .051), "Emitter left its vertical face slab.");
            }
            Require(emitted[0].MinPos.Z < 30.07 && emitted[1].MinPos.Z > 30.93
                && emitted[2].MinPos.X < 10.07 && emitted[3].MinPos.X > 10.93,
                "Saturated-face mapping changed.");
            now = 1250; tick(.25f);
            Require(emitted.Count == 4, "Tick polling bypassed the one-second emission throttle.");
            now = 2000; tick(.25f);
            Require(emitted.Count == 8, "Visible reveal stopped emitting before expiry.");
            Clear();
            Reveal(Packet(50)); now = 3000; tick(.25f);
            Require(emitted.Count == 0, "Distant groundwater emitted particles.");
            Clear();
            Reveal(Packet(duration: 1000)); now = 4000; tick(.25f);
            Require(emitted.Count == 0, "Expired groundwater emitted particles.");
            Reveal(new GroundwaterReveal { Dimension = 1, ExcavationExposure = true, Radius = 8 });
            Reveal(new GroundwaterReveal { Dimension = 0, ExcavationExposure = false, Radius = 8 });
            now = 5000; tick(.25f);
            Require(emitted.Count == 0, "Invalid or non-excavation reveal emitted particles.");
            for (var i = 0; i < 20; i++) Reveal(Packet(i));
            now = 6000; tick(.25f);
            Require(emitted.Count == 48 && emitted.Sum(p => p.MinQuantity + p.AddQuantity) == 240,
                "Reveal retention bypassed its twelve-reveal particle budget.");
        }
        finally { renderer.Dispose(); }
        Require(unregistered, "Groundwater listener remained registered after disposal.");
        Console.WriteLine("PASS groundwater native drip size, downward face emitters, throttle, distance, expiry and particle budget");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
