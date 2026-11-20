using System.Collections;
using System.Reflection;
using Seraph;
using Seraph.Core.State;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class GroundDrinkChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var slot = new DummySlot();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : throw new Exception(m.Name));
        var entity = new EntityPlayer { Alive = true };
        entity.Pos.SetPos(.5, 100, .5); entity.Pos.Pitch = -.3f; entity.Pos.Yaw = 0;
        entity.ServerControls.ShiftKey = true;
        var player = FixturePlayer.Make(entity, inventory);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        var water = new BlockWater { Code = new("game:water-still-7"), BlockId = 1, LiquidCode = "water" };
        var air = new Block { Code = new("game:air"), BlockId = 0 };
        var bank = new Block { Code = new("game:rock-granite"), BlockId = 2 };
        BlockBarrel? placedBarrel = null;
        BlockEntityBarrel? placedBarrelEntity = null;
        var obstructed = false;
        // Visible water at five blocks: within the hover ray but outside the former interaction range.
        var waterPos = new BlockPos(0, 98, -5, 0);
        var accessor = Proxy<IBlockAccessor>((m, a) => m.Name switch {
            "GetBlock" => a!.Length > 1 && (int)a[1]! == BlockLayersAccess.Fluid
                ? ((BlockPos)a[0]!).Equals(waterPos) ? water : air
                : ((BlockPos)a[0]!).Equals(waterPos) && placedBarrel is not null ? placedBarrel
                : obstructed && ((BlockPos)a[0]!).Z == -2 ? bank : air,
            "GetBlockEntity" => ((BlockPos)a![0]!).Equals(waterPos) ? placedBarrelEntity : null,
            "GetClimateAt" => null,
            _ => throw new Exception("Drink accessor: " + m.Name)
        });
        long milliseconds = 1000;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name switch { "get_TotalHours" => (object)10d, "get_HoursPerDay" => 24f, _ => throw new Exception(m.Name) });
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? true : throw new Exception(m.Name));
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_ElapsedMilliseconds" => milliseconds, "get_Calendar" => calendar, "get_BlockAccessor" => accessor,
            "get_Claims" => claims, "get_SeaLevel" => 100, "PlayerByUid" => player,
            _ => throw new Exception("Drink world: " + m.Name)
        });
        entity.World = world;
        var save = RoutingProxy.Make(typeof(IWorldManagerAPI).GetProperty("SaveGame")!.PropertyType,
            (m, _) => m.Name == "get_SavegameIdentifier" ? "drink-world" : throw new Exception(m.Name));
        var manager = Proxy<IWorldManagerAPI>((m, _) => m.Name == "get_SaveGame" ? save : throw new Exception(m.Name));
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_WorldManager" => manager, "get_Logger" => logger, _ => throw new Exception(m.Name)
        });
        var type = typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var coordinator = Activator.CreateInstance(type, [api])!;
        var sessionType = type.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        ((IDictionary)type.GetField("sessions", flags)!.GetValue(coordinator)!).Add(player.PlayerUID, session);
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(coordinator, args);
        T Get<T>(string name) => (T)sessionType.GetProperty(name)!.GetValue(session)!;
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        Set("Environment", EnvironmentSample.Neutral);
        void Begin() { var result = (TextCommandResult)Call("BeginHeldDrink", player, session)!; Require(result.Status == EnumCommandStatus.Success, result.StatusMessage); }
        void Tick(bool renew = true) { milliseconds += 250; if (renew) Set("LastDrinkHoldSeconds", milliseconds / 1000d); Call("TickGroundDrink", session, true, .25d); }
        var initial = Get<BodyState>("State");
        obstructed = true;
        var rejected = (TextCommandResult)Call("BeginHeldDrink", player, session)!;
        Require(rejected.Status == EnumCommandStatus.Error && Get<BodyState>("State") == initial
            && sessionType.GetProperty("GroundDrink")!.GetValue(session) is null, "Aiming at the bank started drinking water behind it.");
        obstructed = false;
        Begin(); obstructed = true; Tick();
        Require(sessionType.GetProperty("GroundDrink")!.GetValue(session) is null && Get<BodyState>("State") == initial,
            "A solid obstruction did not interrupt held drinking.");
        obstructed = false;
        Begin();
        for (var i = 0; i < 11; i++) Tick();
        Require(Get<BodyState>("State").GutWaterL == initial.GutWaterL, "Drinking paid before three real seconds.");
        Tick();
        var completed = Get<BodyState>("State");
        var completedFrame = Get<FrameworkState>("Framework");
        Require(Math.Abs(completedFrame.Barriers.Ingested.GetValueOrDefault("water-enteric")
                - 2 * (1 - ExposureBarriers.IngestionPassThrough)) < 1e-9
            && completedFrame.Diseases.Length == 1 && completedFrame.Diseases[0].Pathogen == "water-enteric"
            && Math.Abs(completedFrame.Barriers.Ledger.Last().Overflow - 2 * ExposureBarriers.IngestionPassThrough) < 1e-9,
            "Actual surface drinking applied the source dose twice or bypassed barriers.");
        Require(Math.Abs(completedFrame.Nutrition.PendingWaterWasteMl) < 1e-9
            && completedFrame.Blood.WasteMl == 0, "Drink turbidity became blood waste.");
        Require(Math.Abs(completed.GutWaterL - initial.GutWaterL - .25) < 1e-9 && completed.BodyWaterL == initial.BodyWaterL,
            "Completed water did not enter the stomach exactly once.");
        Tick(); Require(ReferenceEquals(completed, Get<BodyState>("State")), "Completed gesture paid again.");
        milliseconds += 5000; Begin(); entity.ServerControls.ShiftKey = false; Tick();
        Require(sessionType.GetProperty("GroundDrink")!.GetValue(session) is null && ReferenceEquals(completed, Get<BodyState>("State")), "Release did not cancel.");
        entity.ServerControls.ShiftKey = true; Begin();
        for (var i = 0; i < 4; i++) Tick(false);
        Require(sessionType.GetProperty("GroundDrink")!.GetValue(session) is null, "Lost heartbeat completed drinking.");
        Begin(); player.FailSave = true;
        var failed = false;
        try { for (var i = 0; i < 12; i++) Tick(); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { failed = true; }
        Require(failed && ReferenceEquals(completed, Get<BodyState>("State")), "Failed checkpoint retained water intake.");
        Require(ReferenceEquals(completedFrame, Get<FrameworkState>("Framework")), "Failed checkpoint retained a water waste budget.");
        var barrelBlock = new BlockBarrel { Code = new("game:barrel"), BlockId = 61 };
        typeof(CollectibleObject).GetField("api", flags)!.SetValue(barrelBlock, api);
        placedBarrelEntity = new BlockEntityBarrel { Block = barrelBlock, Pos = waterPos, Api = api, Sealed = false };
        placedBarrelEntity.Inventory.Api = api;
        var waterItem = new Item { Code = new("game:waterportion"), ItemId = 62,
            Attributes = Vintagestory.API.Datastructures.JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        placedBarrelEntity.Inventory[1].Itemstack = new ItemStack(waterItem, 500);
        placedBarrel = barrelBlock;
        Begin();
        Require(sessionType.GetProperty("GroundDrink")!.GetValue(session) is not null,
            "Shift+right-click did not start drinking from an open wooden barrel.");
        Tick();
        Require(sessionType.GetProperty("GroundDrink")!.GetValue(session) is not null,
            "Open barrel did not remain a valid source while the drink action was held.");
        entity.ServerControls.ShiftKey = false; Tick();
        Require(sessionType.GetProperty("GroundDrink")!.GetValue(session) is null,
            "Releasing the barrel drink gesture did not cancel it.");
        Console.WriteLine("PASS ground drinking from visible water and open wooden barrels: timing, cancellation, heartbeat and checkpoint rollback");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
