using System.Collections;
using System.Reflection;
using Seraph;
using Seraph.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

internal static class ClosedEyeRestChecks
{
    private sealed class PosePlayer : EntityPlayer
    {
        public override IAnimationManager AnimManager { get; set; } = null!;
        public override TEntityBehavior? GetBehavior<TEntityBehavior>() where TEntityBehavior : class => null;
    }

    internal static void Run(string gamePath)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var entity = new PosePlayer { Alive = true, OnGround = true };
        var poses = new List<AnimationMetaData>();
        entity.AnimManager = Proxy<IAnimationManager>((m, args) => m.Name switch {
            "StartAnimation" => Start((AnimationMetaData)args![0]!),
            "StopAnimation" => Stop((string)args![0]!), _ => throw new Exception(m.Name)
        });
        bool Start(AnimationMetaData pose) { poses.Add(pose); return true; }
        object? Stop(string code) { poses.RemoveAll(p => p.Code == code); return null; }
        var player = FixturePlayer.Make(entity, null!);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        var api = Proxy<ICoreServerAPI>((m, _) => throw new Exception(m.Name));
        var coordinatorType = typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var coordinator = Activator.CreateInstance(coordinatorType, [api])!;
        var sessionType = coordinatorType.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        ((IDictionary)coordinatorType.GetField("sessions", flags)!.GetValue(coordinator)!).Add(player.PlayerUID, session);
        void Call(string method) => coordinatorType.GetMethod(method, flags)!.Invoke(coordinator, [session]);
        var behavior = new ClosedEyeRestBehavior(entity);
        const string attribute = "vsslssmod-closed-eye-rest";
        // Zero pressure is the state that used to cancel K immediately after the success message.
        var body = (Seraph.Core.State.BodyState)sessionType.GetProperty("State")!.GetValue(session)!;
        sessionType.GetProperty("State")!.SetValue(session, body with { SleepPressure = 0 });
        Call("ToggleClosedEyeRest");
        Require(entity.WatchedAttributes.GetInt(attribute) == 2, "Standing K did not sync reclined state.");
        behavior.OnGameTick(.05f); behavior.OnGameTick(.05f);
        Require(poses.Count == 1 && poses[0].Animation == "lie" && poses[0].ClientSide && poses[0].SupressDefaultAnimation,
            "Standing K did not start a persistent native lying pose exactly once.");
        var shape = JsonObject.FromJson(File.ReadAllText(Path.Combine(gamePath,
            "assets/game/shapes/entity/humanoid/seraph-faceless.json"))).AsObject<Shape>()!;
        shape.InitForAnimations(null!, "closed-eye-test");
        foreach (var name in new[] { "lie", "sitflooridle" })
        {
            var animation = shape.Animations.Single(a => a.Code == name);
            animation.GenerateAllFrames(shape.Elements, shape.JointsById);
            Require(animation.PrevNextKeyFrameByFrame.Length > 0, "Rest pose failed to resolve on the real player skeleton.");
        }
        for (var i = 0; i < 100; i++) Call("ValidateClosedEyeRest");
        Require(entity.WatchedAttributes.GetInt(attribute) == 2, "Low-pressure K rest cancelled on validation.");
        Call("ToggleClosedEyeRest"); behavior.OnGameTick(.05f);
        Require(poses.Count == 0 && entity.WatchedAttributes.GetInt(attribute) == 0, "Second K retained pose/state.");
        entity.ServerControls.FloorSitting = true;
        Call("ToggleClosedEyeRest"); behavior.OnGameTick(.05f);
        Require(poses.Single().Animation == "sitflooridle", "G + K lost the seated pose.");
        entity.ServerControls.Forward = true;
        Call("ValidateClosedEyeRest"); behavior.OnGameTick(.05f);
        Require(poses.Count == 0 && entity.WatchedAttributes.GetInt(attribute) == 0, "Movement did not release rest pose.");
        entity.ServerControls.Forward = false; entity.ServerControls.FloorSitting = false;
        Call("ToggleClosedEyeRest"); behavior.OnGameTick(.05f);
        entity.Alive = false;
        Call("ValidateClosedEyeRest"); behavior.OnGameTick(.05f);
        Require(poses.Count == 0, "Death retained the resting pose.");
        Console.WriteLine("PASS actual K server toggle/validation to client pose; lying/seated native skeletons, zero pressure, repeat toggle, movement and death cleanup");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
