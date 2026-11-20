using System.Collections;
using System.Reflection;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

internal static class BathActionChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var slot = new DummySlot();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot
            : throw new Exception("Unexpected bath inventory " + m.Name));
        var entity = new EntityPlayer { Alive = true };
        entity.Pos.SetPos(10.5, 20.125, 30.5);
        var player = FixturePlayer.Make(entity, inventory);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival
            : throw new Exception("Unexpected bath player data " + m.Name));
        var bath = new BlockEntityBathtub { Block = new BlockBathtub { Code = new("vsslssmod:bathtub"), BlockId = 50 }, Pos = new BlockPos(10, 20, 30, 0) };
        int SoapCharges() => (int)typeof(BlockEntityBathtub).GetProperty("SoapChargeCount", flags)!.GetValue(bath)!;
        var part = new BlockEntityBathtub { Block = new BlockBathtub { Code = new("vsslssmod:bathtub-part"), BlockId = 51 }, Pos = bath.Pos.AddCopy(1, 0, 0) };
        var accessor = Proxy<IBlockAccessor>((m, a) => m.Name switch {
            "GetBlock" => ((BlockPos)a![0]!).X == 11 ? part.Block : bath.Block,
            "GetBlockEntity" => ((BlockPos)a![0]!).X == 11 ? part : bath,
            "MarkBlockEntityDirty" or "MarkBlockDirty" => null,
            _ => throw new Exception("Unexpected bath accessor " + m.Name)
        });
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? true : throw new Exception(m.Name));
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_BlockAccessor" => accessor, "get_Claims" => claims, "get_ElapsedMilliseconds" => 1000L,
            "get_Side" => EnumAppSide.Server,
            _ => throw new Exception("Unexpected bath world " + m.Name)
        });
        var saveType = typeof(IWorldManagerAPI).GetProperty("SaveGame")!.PropertyType;
        var save = RoutingProxy.Make(saveType, (m, _) => m.Name == "get_SavegameIdentifier" ? "fixture-world" : throw new Exception(m.Name));
        var manager = Proxy<IWorldManagerAPI>((m, _) => m.Name == "get_SaveGame" ? save : throw new Exception(m.Name));
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Side" => EnumAppSide.Server, "get_WorldManager" => manager, "get_Logger" => logger,
            _ => throw new Exception("Unexpected bath API " + m.Name)
        });
        bath.Api = api; part.Api = api; entity.World = world;
        var type = typeof(BlockBathtub).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var coordinator = Activator.CreateInstance(type, [api])!;
        var sessionType = type.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        object? Call(string method, params object?[] args) => type.GetMethod(method, flags)!.Invoke(coordinator, args);
        object? Get(string name) => sessionType.GetProperty(name)!.GetValue(session);
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        ((IDictionary)type.GetField("sessions", flags)!.GetValue(coordinator)!).Add(player.PlayerUID, session);
        var medical = (MedicalActionCoordinator)type.GetField("medical", flags)!.GetValue(coordinator)!;
        var initial = new WaterBatch { VolumeL = 100, TemperatureC = 25, Turbidity = 0, Source = "surface" };
        typeof(BlockEntityBathtub).GetMethod("RestoreWater", flags)!.Invoke(bath, [initial]);
        Set("Framework", FrameworkState.Create(player.PlayerUID) with { Dirtiness = .8,
            Barriers = new() { Skin = new Dictionary<string, double> { ["skin-fungal"] = 50 },
                Ingested = new Dictionary<string, double> { ["giardia"] = 40 } } });
        Call("TryAutoWash", session);
        Require(Get("Wash") is null, "Standing started a bath action.");
        entity.ServerControls.Sneak = true;
        Call("TryAutoWash", session);
        Require(Get("Wash") is not null && (bool)Call("WashStillValid", session)!, "Shift crouch inside bath did not start a valid wash.");
        Require(medical.Advance(player.PlayerUID, 5.9, 0, true, 10.5, 20.125, 30.5, false) is null
            && bath.WaterLitres == 100, "Bath completed or consumed water before five seconds.");
        entity.ServerControls.Sneak = false;
        Require(!(bool)Call("WashStillValid", session)!, "Standing up did not invalidate washing.");
        entity.ServerControls.FloorSitting = true;
        Require((bool)Call("WashStillValid", session)!, "G floor-sitting is not accepted as bath posture.");
        entity.Pos.X = 12.5;
        Require(!(bool)Call("WashStillValid", session)!, "Leaving bath did not invalidate washing.");
        entity.Pos.X = 11.5;
        Require((bool)Call("WashStillValid", session)!, "Second bath cell cannot use the shared reservoir.");
        entity.Pos.X = 10.5;
        var completed = medical.Advance(player.PlayerUID, 6, 0, true, 10.5, 20.125, 30.5, false);
        Require(completed?.Phase == MedicalActionPhase.Completed, "Five-second bath failed to complete.");
        typeof(BlockEntityBathtub).GetMethod("RestoreWater", flags)!.Invoke(bath, [initial with { VolumeL = 39 }]);
        Require(!(bool)Call("CompleteWash", session)! && bath.WaterLitres == 39,
            "Concurrent withdrawal below 40 L did not prevent bath completion.");
        typeof(BlockEntityBathtub).GetMethod("RestoreWater", flags)!.Invoke(bath, [initial]);
        slot.Itemstack = new ItemStack(new Item { Code = new("vsslssmod:soap") }, 2);
        var addSoap = typeof(BlockEntityBathtub).GetMethod("TryAddSoap", flags)!;
        object? addedSoap;
        try { addedSoap = addSoap.Invoke(bath, [player]); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        Require((bool)addedSoap! && slot.StackSize == 1 && SoapCharges() == 1,
            "Right-click bath soap loading did not consume one item and store a charge.");
        player.FailSave = true;
        Require(!(bool)Call("CompleteWash", session)! && bath.WaterLitres == 100
            && SoapCharges() == 1 && ((FrameworkState)Get("Framework")!).Dirtiness == .8,
            "Failed checkpoint lost water/soap or changed cleanliness.");
        Require(((FrameworkState)Get("Framework")!).Barriers.Skin["skin-fungal"] == 50
            && ((FrameworkState)Get("Framework")!).Barriers.Ingested["giardia"] == 40,
            "Failed bath checkpoint changed route barriers.");
        player.FailSave = false;
        Require((bool)Call("CompleteWash", session)! && bath.WaterLitres == 90
            && SoapCharges() == 0 && ((FrameworkState)Get("Framework")!).Dirtiness == 0,
            "Completed bath did not clean and consume water/soap exactly once.");
        Require(((FrameworkState)Get("Framework")!).Treatments.Length == 0,
            "Plain soap attached a herbal treatment to the completed bath wash.");
        Require(((FrameworkState)Get("Framework")!).Barriers.Skin["skin-fungal"] == 5
            && ((FrameworkState)Get("Framework")!).Barriers.Ingested["giardia"] == 40,
            "Bath failed to clean skin barrier or incorrectly cured ingestion.");
        Require(!(bool)Call("CompleteWash", session)! && bath.WaterLitres == 90, "Completed bath submitted twice.");
        var bathWater = (WaterBatch)typeof(BlockEntityBathtub).GetProperty("Water", flags)!.GetValue(bath)!;
        Require(bathWater.Turbidity > 0 && bathWater.PathogensPerL.Count == 0 && bathWater.EntericHazardPerL == 0,
            "Dirt created pathogens or failed to enter the bath.");
        Set("Wash", null); Set("WaterWashLatched", false);
        Call("TryAutoWash", session);
        Require(Get("Wash") is null && medical.Get(player.PlayerUID) is null && bath.WaterLitres == 90,
            "Clean player started another wash or consumed more water.");
        foreach (var code in new[] { "vsslssmod:soap-chamomile", "vsslssmod:soap-lavender", "vsslssmod:soap-mint",
            "vsslssmod:soap-sage", "vsslssmod:soap-thyme", "vsslssmod:soap-unknown", "game:soap",
            "soapandlyecontinued:soapportion-lye", "soapandlyecontinued:soapportion-honey" })
        {
            slot.Itemstack = new ItemStack(new Item { Code = new(code) }, 2);
            Require(!(bool)addSoap.Invoke(bath, [player])! && slot.StackSize == 2 && SoapCharges() == 0,
                "Bath accepted a removed or unrelated soap: " + code);
        }
        slot.Itemstack = new ItemStack(new Item { Code = new("vsslssmod:soap-powder") }, 2);
        Require((bool)addSoap.Invoke(bath, [player])! && slot.StackSize == 1 && SoapCharges() == 1,
            "Bath no longer accepts soap powder.");
        foreach (var code in new[] { "soapandlyecontinued:soapbar-lye", "soapandlyecontinued:soapbar-honey" })
        {
            slot.Itemstack = new ItemStack(new Item { Code = new(code) }, 2);
            Require((bool)addSoap.Invoke(bath, [player])! && slot.StackSize == 1 && SoapCharges() == 2,
                "Bath rejected a supported Soap and Lye bar: " + code);
            bath.TrySpendSoapCharge();
        }
        var savedSoap = new TreeAttribute(); bath.ToTreeAttributes(savedSoap);
        bath.FromTreeAttributes(savedSoap, world);
        var roundTripSoap = new TreeAttribute(); bath.ToTreeAttributes(roundTripSoap);
        Require(savedSoap.GetString("soapCharges") == roundTripSoap.GetString("soapCharges"), "Release soap roundtrip changed quantities.");
        var rejected = new BlockEntityBathtub();
        var oldTree = new TreeAttribute(); oldTree.SetDouble("waterVolume", 80); oldTree.SetString("soapCharges", "{\"mint\":5}");
        try { rejected.FromTreeAttributes(oldTree, world); throw new Exception("RC bath accepted."); }
        catch (InvalidDataException) { }
        try { rejected.ToTreeAttributes(new TreeAttribute()); throw new Exception("Rejected bath could overwrite old state."); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS release bath soap roundtrip and RC read/write rejection");
        typeof(BlockEntityBathtub).GetMethod("RestoreWater", flags)!.Invoke(bath, [initial with { Turbidity = 1 }]);
        Set("Framework", FrameworkState.Create(player.PlayerUID) with { Dirtiness = .3 });
        Call("BeginBathWash", session, bath);
        Require(Get("Wash") is null && bath.WaterLitres == 100, "Residual dirt at the dirty-water floor triggered repeated baths.");
        var plainBath = new TreeAttribute(); bath.ToTreeAttributes(plainBath);
        plainBath.SetString("soapCharges", "{}"); bath.FromTreeAttributes(plainBath, world);
        // Residue follows the calibrated turbidity tiers (.30/.16/.10) through
        // Sanitation.WaterResidue's 30% solids conversion.
        foreach (var (sourceKind, floor) in new[] { ("pond", .09), ("lake", .048), ("river", .03) })
        {
            var surface = SurfaceWater.Apply(initial, sourceKind, new());
            typeof(BlockEntityBathtub).GetMethod("RestoreWater", flags)!.Invoke(bath, [surface]);
            Set("Framework", FrameworkState.Create(player.PlayerUID) with { Dirtiness = .3 });
            Call("BeginBathWash", session, bath);
            Require(Get("Wash") is not null && (bool)Call("CompleteWash", session)!, "Ordinary " + sourceKind + " water blocked bathing.");
            Require(Math.Abs(((FrameworkState)Get("Framework")!).Dirtiness - floor) < 1e-10 && bath.WaterLitres == 90,
                "Bath left pathogen-derived dirt or consumed the wrong volume.");
            Require(((FrameworkState)Get("Framework")!).Diseases.Length == 0, "Bathing swallowed enteric pathogens.");
            medical.Cancel(player.PlayerUID);
            Call("BeginBathWash", session, bath);
            Require(Get("Wash") is null && bath.WaterLitres == 90, "Water residue retriggered a useless bath.");
        }
        Console.WriteLine("PASS ordinary pond/lake/river baths clean to physical residue, preserve oral barriers and reject no-op repeats");
        var kernel = new FrameworkKernel();
        var agentsInWater = new Dictionary<string, double> { ["tetanus"] = .02, ["anthrax"] = .02,
            ["wound-staph"] = .02, ["shigella"] = .02, ["giardia"] = .02, ["salmonella"] = .02 };
        foreach (var kind in new WoundKind?[] { null, WoundKind.Contusion, WoundKind.Cut })
        {
            medical.Cancel(player.PlayerUID);
            Set("Wash", null);
            var frame = FrameworkState.Create(player.PlayerUID) with { Dirtiness = .8 };
            if (kind is { } woundKind) frame = kernel.AddWound(frame, woundKind, .2, 0).State;
            if (kind == WoundKind.Cut)
            {
                frame = kernel.AddWound(frame, WoundKind.Cut, .2, 0, "left-hand").State;
                frame = kernel.AddWound(frame, WoundKind.Cut, .2, 0, "right-leg").State;
            }
            Set("Framework", frame);
            typeof(BlockEntityBathtub).GetMethod("RestoreWater", flags)!.Invoke(bath,
                [initial with { PathogensPerL = agentsInWater }]);
            Call("BeginBathWash", session, bath);
            Require(Get("Wash") is not null && (bool)Call("CompleteWash", session)!, "Contaminated bath failed.");
            frame = (FrameworkState)Get("Framework")!;
            Require(frame.SkinPathogens.Count == 6 && frame.Diseases.Length == 0,
                "Skin carriage itself created disease exposure or lost agent identities.");
            var ongoing = new HashSet<string>();
            var progressed = DiseaseExposure.AdvanceWounds(kernel, frame, 8, .001, ongoing);
            Require(ongoing.SetEquals(kind == WoundKind.Cut ? new[] { "tetanus", "anthrax", "wound-staph" } : []),
                "Bath exposed intact/closed skin, swallowed bathwater, or lost a real open-wound exposure.");
            Require(progressed.Diseases.All(d => !Pathogens.Enteric(d.Pathogen)), "Bath fabricated oral infection.");
            if (kind == WoundKind.Cut)
                foreach (var id in agentsInWater.Keys.Where(Pathogens.Wound))
                    Require(Math.Abs(frame.Wounds.Sum(w => w.Pathogens.GetValueOrDefault(id)) - agentsInWater[id] * .25) < 1e-10,
                        "Bath contact dose multiplied by the number of open wounds.");
            if (kind == WoundKind.Contusion)
            {
                // Legacy contaminated bruises must not continue generating exposure after upgrade.
                frame = frame with { Wounds = frame.Wounds.Select(w => w with { Pathogens = agentsInWater }).ToArray() };
                Require(DiseaseExposure.AdvanceWounds(kernel, frame, 8, .001, new HashSet<string>()).Diseases.Length == 0,
                    "Legacy closed-wound contamination kept generating exposure.");
            }
        }
        medical.Cancel(player.PlayerUID);
        Console.WriteLine("PASS six-agent bath: skin carriage is not infection, closed injuries reject exposure, open cuts retain only actual wound agents");
        var produce = new ItemStack(new Item { Code = new("game:vegetable-carrot"),
            NutritionProps = new() { FoodCategory = EnumFoodCategory.Vegetable, Satiety = 100 } }, 3);
        produce.Attributes.SetString("vsslssmod:pathogens-v1", """{"salmonella":0.8}""");
        slot.Itemstack = produce;
        var remainingWater = 1d;
        var sourceType = type.GetNestedType("WaterSource", flags)!;
        object Source(WaterBatch batch) => Activator.CreateInstance(sourceType, flags, null,
            [batch, "game:waterportion", (System.Func<double, bool>)(amount => { remainingWater -= amount; return true; }),
                (Action)(() => remainingWater = 1)], null)!;
        var waterSource = Source(initial with { VolumeL = 1 });
        Require((bool)Call("CommitProduceWash", session, slot, waterSource)!
            && slot.StackSize == 3 && remainingWater == .25,
            "Produce washing did not wash the whole stack and consume 0.25 L per item.");
        var agents = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
            slot.Itemstack!.Attributes.GetString("vsslssmod:pathogens-v1"))!;
        Require(Math.Abs(agents["salmonella"] - .08) < 1e-9, "Clean water did not remove 90 percent of produce contamination.");
        var beforeFailure = slot.Itemstack.Clone(); remainingWater = 1; player.FailSave = true;
        Require(!(bool)Call("CommitProduceWash", session, slot, waterSource)!
            && slot.StackSize == 3 && remainingWater == 1
            && slot.Itemstack!.Attributes.GetString("vsslssmod:pathogens-v1") == beforeFailure.Attributes.GetString("vsslssmod:pathogens-v1"),
            "Produce checkpoint failure did not roll back item and water.");
        player.FailSave = false;
        var insufficient = Source(initial with { VolumeL = .5 });
        Require(!(bool)Call("CommitProduceWash", session, slot, insufficient)! && remainingWater == 1 && slot.StackSize == 3,
            "Insufficient water partially washed the stack.");
        var dirtySource = Source(initial with { VolumeL = 1, PathogensPerL = new Dictionary<string, double> { ["ecoli"] = 2 } });
        Require((bool)Call("CommitProduceWash", session, slot, dirtySource)!, "Dirty-water produce wash failed.");
        agents = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
            slot.Itemstack!.Attributes.GetString("vsslssmod:pathogens-v1"))!;
        Require(agents["ecoli"] == .5 && !agents.ContainsKey("giardia"), "Dirty water invented or lost pathogen identity.");
        Console.WriteLine("PASS whole-stack produce washing, per-item water and pathogen dose, insufficient water and checkpoint rollback");
        Console.WriteLine("PASS timed bath: Shift/G posture, shared occupancy, interruption, exact consumption, checkpoint rollback and no clean-player repeats");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
