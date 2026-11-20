using System.Collections;
using System.Reflection;
using Seraph;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

internal static class MedicineUseChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var slot = new DummySlot();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : throw new Exception(m.Name));
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, inventory);
        entity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        long milliseconds = 1000;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? 10d : throw new Exception(m.Name));
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_ElapsedMilliseconds" => milliseconds, "get_Calendar" => calendar, "PlayerByUid" => player,
            _ => throw new Exception("Medicine world: " + m.Name)
        });
        entity.World = world;
        var saveType = typeof(IWorldManagerAPI).GetProperty("SaveGame")!.PropertyType;
        var save = RoutingProxy.Make(saveType, (m, _) => m.Name == "get_SavegameIdentifier" ? "medicine-world" : throw new Exception(m.Name));
        var manager = Proxy<IWorldManagerAPI>((m, _) => m.Name == "get_SaveGame" ? save : throw new Exception(m.Name));
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_WorldManager" => manager, "get_Logger" => logger,
            _ => throw new Exception("Medicine API: " + m.Name)
        });
        var type = typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var coordinator = Activator.CreateInstance(type, [api])!;
        var sessionType = type.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        ((IDictionary)type.GetField("sessions", flags)!.GetValue(coordinator)!).Add(player.PlayerUID, session);
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(coordinator, args);
        T Get<T>(string name) => (T)sessionType.GetProperty(name)!.GetValue(session)!;
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        var medicine = new FixtureMedicine { Code = new("vsshmod:medicine-fixture"), ItemId = 900, MaxStackSize = 32 };
        var def = new TreatmentDefinition { Id = "fixture", Group = "fixture", WaterL = .225, AlcoholL = .025 };
        slot.Itemstack = new ItemStack(medicine, 5);
        slot.Itemstack.Attributes.SetDouble("vsshmod:chemicalDose", .02);
        TreatmentUseResult Begin(TreatmentDefinition? d = null) => (TreatmentUseResult)Call("BeginTreatment", player, slot, d ?? def, (d ?? def).Route)!;
        TreatmentUseResult Complete() => (TreatmentUseResult)Call("CompleteTreatment", player, slot)!;
        void Hold(int steps) { for (var i = 0; i < steps; i++) { milliseconds += 500; Require((bool)Call("ContinueTreatment", player, slot)!, "Valid hold interrupted."); } }
        Require(Begin().Success, "Held dose rejected.");
        Require(((TreatmentUseResult)Call("QueryTreatment", player, slot, def, def.Route)!).Success, "Read-only query failed.");
        Hold(6);
        var body = Get<BodyState>("State");
        Require(Complete().Success && slot.StackSize == 4, "Three-second dose did not consume exactly one.");
        Require(Math.Abs(Get<BodyState>("State").GutWaterL - body.GutWaterL - .225) < 1e-9
            && Get<FrameworkState>("Framework").ChemicalBurden == .02
            && Get<FrameworkState>("Framework").AlcoholBurden == .5, "Water, chemical or alcohol dose incorrect.");
        Require(!Complete().Success && slot.StackSize == 4, "Completion replay consumed a second item.");
        Require(Begin().Success && !Complete().Success && slot.StackSize == 4, "Early release consumed medicine.");
        Require(Begin().Success, "Second action rejected.");
        entity.Pos.X += 1;
        Require(!(bool)Call("ContinueTreatment", player, slot)! && !Complete().Success, "Movement failed to interrupt.");
        Require(Begin().Success, "Action after movement rejected.");
        milliseconds += 1500;
        Require(!Complete().Success, "Missing hold heartbeat accepted.");
        Require(Begin().Success, "Action after timeout rejected.");
        Set("LifeGeneration", 1L);
        Require(!(bool)Call("ContinueTreatment", player, slot)!, "Old life accepted.");
        Require(Begin().Success, "New life action rejected.");
        entity.Alive = false;
        Require(!Complete().Success, "Death did not interrupt."); entity.Alive = true;
        Require(Begin().Success, "Revived action rejected."); Hold(6);
        body = Get<BodyState>("State"); var frame = Get<FrameworkState>("Framework");
        player.FailSave = true;
        Require(Complete().Message == "treatment-save-failed" && slot.StackSize == 4
            && ReferenceEquals(body, Get<BodyState>("State")) && ReferenceEquals(frame, Get<FrameworkState>("Framework")), "Checkpoint failure lost dose or physiology.");
        player.FailSave = false;
        var topical = def with { Route = TreatmentRoute.Topical, WaterL = 0, AlcoholL = 0 };
        Require(Begin(topical).Success, "Topical action rejected."); Hold(6);
        Require(!Complete().Success && slot.StackSize == 4, "Topical dose completed before five seconds.");
        Require(Begin(topical).Success, "Second topical action rejected."); Hold(10);
        Require(Complete().Success && slot.StackSize == 3, "Five-second topical dose failed.");
        Require(!Begin(topical with { RequiresWound = true }).Success, "Missing target wound accepted.");
        Set("Deceased", true);
        Require(!Begin().Success, "Deceased player started treatment.");
        Console.WriteLine("PASS medicine use: read-only query, real timing, single consumption, water/alcohol/chemicals, replay, movement, heartbeat, death/life changes and checkpoint rollback");
    }
    private sealed class FixtureMedicine : Item
    {
        public override TransitionState UpdateAndGetTransitionState(IWorldAccessor world, ItemSlot slot, EnumTransitionType type) => null!;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
