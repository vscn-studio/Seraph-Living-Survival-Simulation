using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Seraph;
using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class VehicleCompatibilityChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class Rider : EntityPlayer
    {
        public override IAnimationManager AnimManager { get; set; } = null!;
        public override ItemSlot RightHandItemSlot => new DummySlot();
        internal void Seat(IMountableSeat? seat) => MountedOn = seat!;
    }
    // Suppress only animation/sound work. Native gait movement and rideable permission branches execute normally.
    private sealed class Gait(Entity entity) : EntityBehaviorGait(entity)
    { protected override void OnGaitChangedFrom(GaitMeta? prevGait) { } }
    private static FunctionSnapshot? functions;
    private static bool Functions(ref FunctionSnapshot? __result) { __result = functions; return false; }
    private static bool JauntPropertyName(ref string __result) { __result = "jaunt:gait"; return false; }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    internal static void Run(Assembly mod)
    {
        var bridgeType = mod.GetType("Seraph.Server.NativeVehicleBridge", true)!;
        var activity = mod.GetType("Seraph.Server.ActivityCompatibility", true)!;
        object? Call(string name, params object?[] args) => bridgeType.GetMethod(name, Flags)!.Invoke(null, args);
        bool Ready(string name) => (bool)bridgeType.GetProperty(name, Flags)!.GetValue(null)!;
        using var bridge = (IDisposable)Activator.CreateInstance(bridgeType, true)!;
        Require(Ready("NativeReady"), "Native vehicle IL permission gate did not install.");
        var fixture = new Harmony("vsslssmod.tests.vehicle-functions");
        fixture.Patch(mod.GetType("Seraph.Server.NativeInjuryActionBridge", true)!.GetMethod("Functions", Flags),
            prefix: new(typeof(VehicleCompatibilityChecks), nameof(Functions)));
        try
        {
            long now = 1000;
            var side = EnumAppSide.Server;
            var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, _) => m.Name switch {
                "get_Side" => side, "get_ElapsedMilliseconds" => now, "get_Rand" => new Random(1), _ => throw new Exception(m.Name)
            });
            var system = new SeraphModSystem();
            var loader = (IModLoader)RoutingProxy.Make(typeof(IModLoader), (m, _) => m.Name == "GetModSystem" ? system : throw new Exception(m.Name));
            var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m, _) => m.Name switch {
                "get_Side" => side, "get_World" => world, "get_ModLoader" => loader, _ => throw new Exception(m.Name)
            });
            void Setup(Entity e)
            {
                e.World = world; e.Api = api;
                e.AnimManager = (IAnimationManager)RoutingProxy.Make(typeof(IAnimationManager), (m, _) => m.ReturnType == typeof(bool) ? false : null);
                typeof(Entity).GetProperty("Properties")!.SetValue(e, new EntityProperties {
                    Client = new EntityClientProperties([], new()), Server = new EntityServerProperties([], new())
                });
            }
            var rider = new Rider { EntityId = 10 }; Setup(rider);
            var cart = new EntityAgent { Code = new("cartwrightscaravan:cart-wood-oak"), Alive = true }; Setup(cart);
            var gait = new Gait(cart);
            var idle = new GaitMeta { Code = "idle", Environment = EnumHabitat.Land };
            var walk = new GaitMeta { Code = "walk", Environment = EnumHabitat.Land, MoveSpeed = .02f };
            var sprint = new GaitMeta { Code = "sprint", Environment = EnumHabitat.Land, MoveSpeed = .05f };
            foreach (var g in new[] { idle, walk, sprint }) gait.Gaits[g.Code] = g;
            gait.IdleGaits[EnumHabitat.Land] = idle; gait.CurrentGait = walk;
            var ride = new EntityBehaviorRideable(cart);
            typeof(EntityBehaviorRideable).GetField("ebg", Flags)!.SetValue(ride, gait);
            cart.AddBehavior(gait); cart.AddBehavior(ride);
            var seat = new EntityRideableSeat(ride, "driver", new() { Controllable = true, MountRotation = new(), SeatId = "driver" }) { Passenger = rider };
            ride.Seats = [seat]; rider.Seat(seat);
            functions = new() { Manipulation = true, HandCapacity = .5 };
            (ExertionKind Kind, bool Active) Observe()
            {
                var o = activity.GetMethod("Observe", Flags)!.Invoke(null, [rider, functions])!;
                return ((ExertionKind)o.GetType().GetProperty("Kind")!.GetValue(o)!, (bool)o.GetType().GetProperty("Active")!.GetValue(o)!);
            }
            bool Held() => (bool)activity.GetMethod("TriggerHeld", Flags)!.Invoke(null, [rider])!;
            ride.SeatsToMotion(.033f);
            Require(Observe() == (ExertionKind.Carrying, true) && !Held(), "Persistent cart gait not charged or prevents released-key recovery.");
            // No displacement: pushing into a wall still costs work.
            Require(cart.Pos.Motion.Length() == 0 && Observe().Active, "Cart work inferred from velocity.");
            seat.Controls.Left = true;
            var angle = ride.SeatsToMotion(.033f);
            Require(angle > 0 && angle < .1, "Native turn output/hand capacity not applied.");
            seat.Controls.Left = false;
            gait.CurrentGait = idle; cart.Pos.Motion.Set(.2, .03, .1);
            Require(!Observe().Active, "Idle cart inertia charged as work.");
            var passengerSeat = new EntityRideableSeat(ride, "passenger", new() { Controllable = false, MountRotation = new() }) { Passenger = rider };
            rider.Seat(passengerSeat); passengerSeat.Controls.Forward = true;
            Require(!Observe().Active && !Held(), "Passenger controls charged as driver work.");
            rider.Seat(seat);
            gait.CurrentGait = sprint; ride.SeatsToMotion(.033f);
            Require(Observe() == (ExertionKind.Sprinting, true), "Cart sprint gait not observed.");
            functions.CanSprint = false; ride.SeatsToMotion(.033f);
            Require(ReferenceEquals(gait.CurrentGait, walk), "Cart sprint denial did not select native walk gait.");
            rider.Stats.Set("walkspeed", "sitWalkingBonus", .2f);
            rider.Stats.Set("walkspeed", "carryon", -.1f);
            rider.Stats.Set("walkspeed", "vsslssmod:function", -.3f);
            gait.MoveSpeedModifier = .75;
            var move = typeof(EntityBehaviorGait).GetMethod("Move", Flags)!;
            move.Invoke(gait, [.033f]);
            var speed = cart.Controls.WalkVector.Length();
            // Compare native output with neutral player stats, then restore them: proves exact one-time blend.
            rider.Stats.Set("walkspeed", "vsslssmod:function", -.1f);
            move.Invoke(gait, [.033f]); var neutral = cart.Controls.WalkVector.Length();
            Require(Math.Abs(speed / neutral - .8) < 1e-6 && gait.MoveSpeedModifier == .75
                && Math.Abs(cart.Pos.Motion.X - .2) < 1e-10, "Cart native stats applied twice or changed its modifier/inertia.");
            var activeFunctions = functions;
            rider.Stats.Set("walkspeed", "vsslssmod:function", -.3f);
            functions = null; ride.SeatsToMotion(.033f); move.Invoke(gait, [.033f]);
            Require(Math.Abs(cart.Controls.WalkVector.Length() - neutral) < 1e-10,
                "Observe/disabled mode changed native cart drive.");
            functions = activeFunctions;
            functions.CanUseActiveHand = false;
            seat.Controls.Forward = true; ride.SeatsToMotion(.033f); move.Invoke(gait, [.033f]);
            Require(gait.IsIdle && cart.Controls.WalkVector.Length() == 0 && !Observe().Active && seat.Controls.Forward,
                "Exhaustion failed to stop new cart drive or altered held input.");
            seat.Controls.Forward = false; functions.CanUseActiveHand = true; functions.CanSprint = true;
            gait.CurrentGait = walk; ride.SeatsToMotion(.033f);
            CanRideDelegate deny = (IMountableSeat _, out string? error) => { error = "external-stamina"; return false; };
            ride.CanRide += deny; seat.Controls.Forward = true; ride.SeatsToMotion(.033f);
            Require(gait.IsIdle && !Observe().Active, "External native CanRide rejection was bypassed.");
            ride.CanRide -= deny; seat.Controls.Forward = false;
            ride.CanTurn += deny; seat.Controls.Left = true;
            Require(ride.SeatsToMotion(.033f) == 0 && !Observe().Active, "External CanTurn rejection charged/turned cart.");
            ride.CanTurn -= deny; seat.Controls.Left = false;
            cart.Code = new("game:elk-tamed"); gait.CurrentGait = walk; seat.Controls.Forward = true;
            ride.SeatsToMotion(.033f);
            Require(Observe() == (ExertionKind.MountedControl, true), "Native/Jaunt rideable controller work absent.");
            seat.Controls.Forward = false; ride.SeatsToMotion(.033f);
            Require(!Observe().Active, "Animal persistent gait charged as human pushing.");
            cart.Stats.Set("walkspeed", "jaunt:walkspeed", .42f); cart.WatchedAttributes.SetFloat("jaunt:stamina", .63f);
            functions.CanUseActiveHand = false; ride.SeatsToMotion(.033f);
            Require(ReferenceEquals(gait.CurrentGait, walk) && Math.Abs(cart.Stats.GetBlended("walkspeed") - 1.42f) < 1e-6
                && cart.WatchedAttributes.GetFloat("jaunt:stamina") == .63f, "Human gate overwrote animal gait/stat/stamina.");
            functions.CanUseActiveHand = true;
            now += 501;
            Require(((ValueTuple<bool, bool>)Call("Accepted", seat)!) == (false, false), "Stale accepted seat activity leaked.");
            side = EnumAppSide.Client;
            // Use unchanged inputs and accepted native permissions on both sides.
            Call("FilterMove", true, ride, seat); Call("FilterTurn", true, ride, seat); seat.Controls.Left = true;
            var clientObserved = Observe(); side = EnumAppSide.Server;
            Require(clientObserved == Observe(), "Client/server use different mounted observations.");
            rider.Seat(null); Require(!Observe().Active, "Dismount left active work.");
            Console.WriteLine("PASS vehicle native hooks: cart toggle/blocked pushing, sprint/hand gates, passengers/inertia, stat ownership, native rejections and shared observation");
            CheckShip(bridge, bridgeType, rider, Setup, functions, activity);
            CheckJaunt(bridge, bridgeType, rider, Setup, functions, activity, world);
            using var stream = new MemoryStream();
            ProtoBuf.Serializer.Serialize(stream, new FunctionSnapshot { HandCapacity = .37 }); stream.Position = 0;
            Require(ProtoBuf.Serializer.Deserialize<FunctionSnapshot>(stream).HandCapacity == .37, "Hand capacity protocol append lost.");
            using var old = new MemoryStream();
            Require(ProtoBuf.Serializer.Deserialize<FunctionSnapshot>(old).HandCapacity == 1, "Old protocol did not default hand capacity to neutral.");
        }
        finally { fixture.UnpatchAll(fixture.Id); functions = null; }
        bridge.Dispose();
        Require(!Ready("NativeReady") && !Ready("ShipReady") && !Ready("JauntReady"), "Disposed vehicle adapters still ready.");
    }

    private static void CheckJaunt(IDisposable bridge, Type bridgeType, Rider rider, Action<Entity> setup,
        FunctionSnapshot f, Type activity, IWorldAccessor world)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var zip = ZipFile.OpenRead(Path.Combine(root, "compatibility", "Jaunt_ Extended Riding System-3.3.1.zip"));
        using var bytes = new MemoryStream(); zip.GetEntry("Jaunt.dll")!.Open().CopyTo(bytes); bytes.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(bytes);
        bridgeType.GetMethod("ResolveOptional", Flags)!.Invoke(bridge, null);
        Require((bool)bridgeType.GetProperty("JauntReady", Flags)!.GetValue(null)!, "Exact Jaunt extra jump/flight hooks failed.");
        var animal = new EntityAgent { Code = new("jaunt:testmount"), Alive = true, OnGround = true }; setup(animal);
        var type = assembly.GetType("Jaunt.Behaviors.EntityBehaviorJauntRideable", true)!;
        var ride = (EntityBehaviorRideable)Activator.CreateInstance(type, [animal])!;
        var gait = new Gait(animal);
        var idle = new GaitMeta { Code = "idle", Environment = EnumHabitat.Land };
        gait.Gaits["idle"] = idle; gait.IdleGaits[EnumHabitat.Land] = idle; gait.CurrentGait = idle;
        typeof(EntityBehaviorRideable).GetField("ebg", Flags)!.SetValue(ride, gait);
        animal.AddBehavior(ride); animal.AddBehavior(gait);
        var seat = (EntityRideableSeat)type.GetMethod("CreateSeat", Flags)!.Invoke(ride,
            ["driver", new SeatConfig { Controllable = true, MountRotation = new() }])!;
        seat.Passenger = rider;
        ride.Seats = [seat]; rider.Seat(seat); seat.Controls.Jump = true;
        var method = type.GetMethod("SeatsToMotion")!;
        var jump = typeof(EntityBehaviorRideable).GetField("jumpNow", Flags)!;
        f.CanUseActiveHand = false; method.Invoke(ride, [.033f]);
        Require(!(bool)jump.GetValue(ride)! && seat.Controls.Jump, "Jaunt re-enabled jump after native gate or altered raw input.");
        f.CanUseActiveHand = true; method.Invoke(ride, [.033f]);
        Require((bool)jump.GetValue(ride)!, "Healthy Jaunt jump was lost.");
        // Exercise real Jaunt airborne generation, including clearing last frame's vertical drive.
        var flightType = assembly.GetType("Jaunt.Behaviors.EntityBehaviorJauntGait", true)!;
        var registrationFixture = new Harmony("vsslssmod.tests.jaunt-registration");
        EntityBehaviorGait flight;
        // Only the registration name needs a live Jaunt ModSystem; all flight logic runs unmodified.
        registrationFixture.Patch(flightType.GetMethod("PropertyName"), prefix: new(typeof(VehicleCompatibilityChecks), nameof(JauntPropertyName)));
        try { flight = (EntityBehaviorGait)Activator.CreateInstance(flightType, [animal])!; }
        finally { registrationFixture.UnpatchAll(registrationFixture.Id); }
        var hover = (GaitMeta)Activator.CreateInstance(assembly.GetType("Jaunt.Behaviors.JauntGaitMeta", true)!)!;
        hover.Code = "hover"; hover.Environment = EnumHabitat.Air;
        hover.GetType().GetProperty("AscendSpeed")!.SetValue(hover, 1f);
        hover.GetType().GetProperty("DescendSpeed")!.SetValue(hover, 5f);
        typeof(EntityBehaviorGait).GetField("currentGait", Flags)!.SetValue(flight, hover);
        flight.IdleGaits[EnumHabitat.Air] = hover; flight.Gaits["hover"] = hover;
        typeof(EntityBehaviorRideable).GetField("ebg", Flags)!.SetValue(ride, flight);
        typeof(EntityBehaviorRideable).GetField("coyoteTimer", Flags)!.SetValue(ride, -.2f);
        var vertical = flight.GetType().GetField("VerticalSpeed")!;
        animal.OnGround = false; animal.Controls.IsFlying = true; animal.Pos.Y = 100;
        var air = new Block { Code = new("game:air") };
        var blockAccessor = (IBlockAccessor)RoutingProxy.Make(typeof(IBlockAccessor), (m, _) => m.Name switch {
            "get_MapSizeY" => 256, "GetBlockBelow" or "GetBlock" => air, _ => throw new Exception(m.Name)
        });
        animal.World = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, a) => m.Name == "get_BlockAccessor"
            ? blockAccessor : m.Invoke(world, a));
        method.Invoke(ride, [.033f]);
        Require((double)vertical.GetValue(flight)! > 0 && animal.Controls.Up, "Healthy Jaunt ascent was changed.");
        f.CanUseActiveHand = false; method.Invoke(ride, [.033f]);
        Require((double)vertical.GetValue(flight)! == 0 && !animal.Controls.Up && !animal.Controls.Down
            && animal.Controls.IsFlying && seat.Controls.Jump, "Exhaustion left stale Jaunt ascent or forced animal out of flight.");
        seat.Controls.Jump = false; seat.Controls.Sneak = true; f.CanUseActiveHand = true;
        method.Invoke(ride, [.033f]);
        var observation = activity.GetMethod("Observe", Flags)!.Invoke(null, [rider, f])!;
        Require((double)vertical.GetValue(flight)! < 0 && animal.Controls.Down
            && (bool)observation.GetType().GetProperty("Active")!.GetValue(observation)!
            && (bool)activity.GetMethod("TriggerHeld", Flags)!.Invoke(null, [rider])!, "Jaunt descend control was not charged/held.");
        f.CanUseActiveHand = false; method.Invoke(ride, [.033f]);
        Require((double)vertical.GetValue(flight)! == 0 && !animal.Controls.Down && seat.Controls.Sneak,
            "Exhaustion left Jaunt descent active or changed raw sneak input.");
        f.CanUseActiveHand = true; rider.Seat(null);
        try
        {
            bridgeType.GetMethod("JauntInputs", Flags)!.Invoke(null, [new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) }]);
            throw new Exception("Changed Jaunt IL was accepted.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException) { }
        Console.WriteLine("PASS exact Jaunt 3.3.1 live hook: secondary jump/ascent/descent gate, stale drive clearing, raw controls/animal flight preserved and changed-contract rejection");
    }

    private static void CheckShip(IDisposable bridge, Type bridgeType, Rider rider, Action<Entity> setup,
        FunctionSnapshot f, Type activity)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var archive = Path.Combine(root, "compatibility", "Shipwright_ Vessels of Distant Shores-1.5.1.zip");
        Require(File.Exists(archive), "Exact optional Shipwright test archive missing.");
        using var zip = ZipFile.OpenRead(archive);
        using var bytes = new MemoryStream(); zip.GetEntry("shipwright.dll")!.Open().CopyTo(bytes); bytes.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(bytes);
        bridgeType.GetMethod("ResolveOptional", Flags)!.Invoke(bridge, null);
        Require((bool)bridgeType.GetProperty("ShipReady", Flags)!.GetValue(null)!, "Exact optional Shipwright hooks failed.");
        var boatType = assembly.GetType("shipwright.EntityShipwrightBoat", true)!;
        var boat = (Entity)Activator.CreateInstance(boatType)!; setup(boat); boat.Code = new("shipwright:boat");
        var ride = new EntityBehaviorSeatable(boat); boat.AddBehavior(ride);
        var seatType = assembly.GetType("shipwright.EntityShipwrightBoatSeat", true)!;
        var seat = (EntitySeat)Activator.CreateInstance(seatType, [ride, "helm", new SeatConfig { Controllable = true, MountRotation = new() }])!;
        seat.Passenger = rider; ride.Seats = [seat]; rider.Seat(seat);
        boatType.GetField("CurrentlyControllingEntityId", Flags)!.SetValue(boat, rider.EntityId);
        // A non-null animation manager is unnecessary for active inputs; native boat uses null-conditional calls.
        var animations = (Dictionary<string, string>)boatType.GetField("MountAnimations")!.GetValue(boat)!;
        foreach (var key in new[] { "ready", "backwards", "forwards" }) animations[key] = key;
        var motion = boatType.GetMethod("SeatsToMotion")!;
        Vec2d Step() => (Vec2d)motion.Invoke(boat, [.1f])!;
        (ExertionKind Kind, bool Active) Observe()
        {
            var o = activity.GetMethod("Observe", Flags)!.Invoke(null, [rider, f])!;
            return ((ExertionKind)o.GetType().GetProperty("Kind")!.GetValue(o)!, (bool)o.GetType().GetProperty("Active")!.GetValue(o)!);
        }
        seat.Controls.Forward = true; f.CanUseActiveHand = true; f.HandCapacity = .5;
        var result = Step();
        Require(Math.Abs(result.X - .1) < 1e-6 && Observe() == (ExertionKind.Rowing, true), "Ship manual thrust not scaled/charged.");
        f.CanUseActiveHand = false; result = Step();
        Require(result.X == 0 && !Observe().Active && seat.Controls.Forward, "Exhausted rowing not precluded or input lost.");
        boatType.GetField("unfurlSails", Flags)!.SetValue(boat, true); boat.WatchedAttributes.SetInt("sailPosition", 2);
        result = Step();
        Require(Math.Abs(result.X - .3) < 1e-6, "Exhaustion removed automatic sail propulsion.");
        f.CanUseActiveHand = true; seat.Controls.Forward = false; seat.Controls.Left = true; result = Step();
        Require(Math.Abs(result.X - .3) < 1e-6 && Math.Abs(result.Y - .05) < 1e-6
            && Observe() == (ExertionKind.MountedControl, true), "Sail steering was omitted or counted as rowing.");
        seat.Controls.Left = false; result = Step();
        Require(!Observe().Active && result.X > 0, "Sail-only motion charged as human work.");
        boatType.GetField("requiresPaddlingTool", Flags)!.SetValue(boat, true);
        result = Step(); Require(result.X == 0 && !Observe().Active, "Paddle requirement was bypassed.");
        boatType.GetField("requiresPaddlingTool", Flags)!.SetValue(boat, false);
        boatType.GetField("CurrentlyControllingEntityId", Flags)!.SetValue(boat, 999L);
        result = Step(); Require(!Observe().Active && result.X == 0, "Non-controller charged or drove Shipwright.");
        Console.WriteLine("PASS exact Shipwright 1.5.1 live hook: paddle/controller contract, manual-only drive and steering, sail preservation and protocol defaults");
    }
}
