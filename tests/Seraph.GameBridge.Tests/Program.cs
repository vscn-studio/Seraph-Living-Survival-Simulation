using System.Reflection;
using System.Runtime.Loader;

// Loads the real game assemblies, patches and unpatches an isolated console process. Never starts a game.
var gamePath = Path.GetFullPath(args.Length > 0 ? args[0] : throw new ArgumentException("Supply the VS 1.22.x game directory."));
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (var folder in new[] { gamePath, Path.Combine(gamePath, "Lib"), Path.Combine(gamePath, "Mods") })
    {
        var file = Path.Combine(folder, name.Name + ".dll");
        if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    }
    return null;
};
try
{
    var mod = Assembly.Load("vsslssmod");
    if (args.Contains("--cottage-water")) { Assembly.Load("VintagestoryLib"); CottageWaterChecks.Run(args[Array.IndexOf(args, "--cottage-water") + 1]); return 0; }
    if (args.Contains("--stone-firepit")) { Assembly.Load("VintagestoryLib"); StoneFirepitChecks.Run(); return 0; }
    if (args.Contains("--water-immersion")) { WaterImmersionChecks.Run(); return 0; }
    if (args.Contains("--bed-rest")) {
        Assembly.Load("VintagestoryLib");
        BedRestChecks.InitializeTranslations();
        BedRestChecks.Run(); return 0;
    }
    if (args.Contains("--bleeding")) { BleedingChecks.Run(mod, bandageOnly: true); return 0; }
    if (args.Contains("--food-registry")) { ReleaseFoodChecks.Registry(args[Array.IndexOf(args, "--food-registry") + 1]); return 0; }
    if (args.Contains("--nutrition-handbook")) { NutritionHandbookChecks.Run(args[Array.IndexOf(args, "--nutrition-handbook") + 1]); return 0; }
    if (args.Contains("--smithing-tooltips")) { SmithingTooltipChecks.Run(args[Array.IndexOf(args, "--smithing-tooltips") + 1]); return 0; }
    if (args.Contains("--dark-adaptation")) { DarkAdaptationChecks.Run(); return 0; }
    if (args.Contains("--panels")) {
        var nativeGame = args.Skip(1).First(Directory.Exists);
        HandbookChecks.Run(nativeGame, configureCairo: true);
        CharacterPanelChecks.Run(mod);
        PinnedCharacterPanelChecks.Run(mod, nativeGame);
        PanelPerformanceChecks.Run(mod, nativeGame); return 0;
    }
    if (args.Contains("--body-hud")) { BodyHudChecks.Run(args.Skip(1).First(Directory.Exists)); return 0; }
    if (args.Contains("--status-hud")) { StatusHudChecks.Run(args.Skip(1).First(Directory.Exists)); return 0; }
    if (args.Contains("--performance")) { PerformanceChecks.Run(args.FirstOrDefault(a => a.EndsWith("Komet.Core.dll", StringComparison.OrdinalIgnoreCase))); return 0; }
    if (args.Contains("--runtime-work")) { RuntimeWorkChecks.Run(); return 0; }
    if (args.Contains("--world-interactions")) { Assembly.Load("VintagestoryLib"); LocalizedTextChecks.Run(mod); WaterHoverChecks.Run(); ChimneyChecks.Run(); SmokeBridgeChecks.Run(); StoneFirepitChecks.Run(); return 0; }
    if (args.Contains("--recipes")) { RecipeChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"))); RecipeMetadataChecks.Run(mod, gamePath); return 0; }
    if (args.Contains("--map-hover")) { HandbookChecks.Run(args.Skip(1).First(Directory.Exists), configureCairo: true); MapHoverRenderChecks.Run(); return 0; }
    if (args.Contains("--pollution")) { PollutionRouteChecks.Run(); PondPollutionChecks.Run(); PollutionCaptureChecks.Run(mod); PollutionDebugChecks.Run(); return 0; }
    if (args.Contains("--metrics")) { CharacterMetricChecks.Run(mod); return 0; }
    if (args.Contains("--clothing")) { ClothingChecks.Run(); return 0; }
    if (args.Contains("--fixtures")) { var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")); FixtureChecks.Run(root); FixtureOrientationChecks.Run(); FixtureWaterChecks.Run(root); TreatmentChecks.Run(root, gamePath); return 0; }
    if (args.Contains("--treatment")) { TreatmentChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), gamePath); return 0; }
    if (args.Contains("--butchering-food")) { PreservedFoodChecks.NativeButchering(args[Array.IndexOf(args, "--butchering-food") + 1]); return 0; }
    if (args.Contains("--food")) { FoodChecks.Run(mod); SleepNutritionChecks.Run(mod); MedicineUseChecks.Run(); return 0; }
    if (args.Contains("--flow-sampling")) { BedRestChecks.Run(samplingOnly: true); return 0; }
    if (args.Contains("--labor")) { LocalizedTextChecks.Run(mod); NativeLaborExecutionChecks.Run(mod); return 0; }
    if (args.Contains("--fixture-help")) { FixtureHelpChecks.Run(); return 0; }
    if (args.Contains("--hot-water")) {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        FixtureOrientationChecks.Run(); FixtureWaterChecks.Run(root); CopperBathChecks.Run(root); BathActionChecks.Run(); StoneFirepitChecks.Run(); return 0;
    }
    if (args.Contains("--well-structure"))
    {
        SlowCallbackChecks.Run();
        RuntimeWorkChecks.Run();
        GroundwaterStartupChecks.Run();
        WellStructureChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), gamePath);
        return 0;
    }
    if (args.Contains("--handbook"))
    {
        var nativeGame = args.Skip(1).First(Directory.Exists);
        HandbookChecks.Run(nativeGame, configureCairo: true); return 0;
    }
    var nativeSmoke = Array.IndexOf(args, "--smoke-native");
    if (nativeSmoke >= 0) { SmokeNativeChecks.Run(mod, args[nativeSmoke + 1], args[nativeSmoke + 2]); return 0; }
    if (args.Contains("--latrine")) { LatrineChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), gamePath); return 0; }
    if (args.Contains("--waste")) { WasteBridgeChecks.Run(mod); AshBridgeChecks.Run(mod); return 0; }
    if (args.Contains("--water")) { SurfaceWaterBridgeChecks.Run(); WaterCompatibilityBridgeChecks.Run(mod); WaterInventoryTransferChecks.Run(); WaterInfoChecks.Run(mod); WaterHeatingGameChecks.Run(); DistillationGameChecks.Run(); GroundDrinkChecks.Run(); return 0; }
    if (args.Contains("--symptoms")) { SymptomChecks.Run(mod); SleepEyeCombatChecks.Run(mod); ProgressRedesignChecks.Run(mod, null); return 0; }
    if (args.Contains("--smoke")) { SmokeBridgeChecks.Run(); return 0; }
    WasteBridgeChecks.Run(mod);
    AshBridgeChecks.Run(mod);
    SmokeBridgeChecks.Run();
    NativeStatChecks.Run(mod);
    VehicleCompatibilityChecks.Run(mod);
    LocalizedTextChecks.Run(mod);
    CharacterPanelChecks.Run(mod);
    SleepNutritionChecks.Run(mod);
    ActivityHudChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    HandbookChecks.Run(args.Skip(1).FirstOrDefault(Directory.Exists));
    PinnedCharacterPanelChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    ProgressRedesignChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    DarkAdaptationChecks.Run();
    AdrenalineAudioChecks.Run();
    AdrenalineHudChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    RestTooltipChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    PanelPerformanceChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    var bridgeType = mod.GetType("Seraph.Server.NativeInjuryActionBridge", throwOnError: true)!;
    using (var bridge = (IDisposable)Activator.CreateInstance(bridgeType, nonPublic: true)!)
    {
        var harmonyType = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony")!;
        var patched = (IEnumerable<MethodBase>)harmonyType.GetMethod("GetAllPatchedMethods")!.Invoke(null, null)!;
        // Harmony retains method entries after disposal of render-test hooks.
        var methods = patched.Where(m => {
            var info = harmonyType.GetMethod("GetPatchInfo")!.Invoke(null, [m])!;
            return ((IEnumerable<string>)info.GetType().GetProperty("Owners")!.GetValue(info)!)
                .Contains("vsslssmod.injury-actions");
        }).ToArray();
        if (methods.Length != 10) throw new Exception($"Expected 10 action/oxygen hooks; installed {methods.Length}.");
        foreach (var method in methods) Console.WriteLine($"PASS installed {method.DeclaringType!.Name}.{method.Name}");

        // Verify the jump finalizer restores controls on both success and exception, and scales only a new jump.
        var api = Assembly.Load("VintagestoryAPI");
        var controlsType = api.GetType("Vintagestory.API.Common.EntityControls", true)!;
        var controls = Activator.CreateInstance(controlsType)!;
        var flags = (bool[])controlsType.GetProperty("Flags")!.GetValue(controls)!;
        var posType = api.GetType("Vintagestory.API.Common.Entities.EntityPos", true)!;
        var pos = Activator.CreateInstance(posType)!;
        var motion = posType.GetField("Motion")!.GetValue(pos)!;
        var motionY = motion.GetType().GetField("Y")!;
        var stateType = bridgeType.GetNestedType("GroundState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [true, 10L, .64d], null)!;
        var finalizer = bridgeType.GetMethod("GroundEnd", BindingFlags.Static | BindingFlags.NonPublic)!;
        motionY.SetValue(motion, 1d);
        finalizer.Invoke(null, [null, controls, pos, 11L, state, null]);
        if (!flags[4] || Math.Abs((double)motionY.GetValue(motion)! - .8) > 1e-10) throw new Exception("New jump not scaled/restored.");
        flags[4] = false; motionY.SetValue(motion, -.2d);
        var error = new InvalidOperationException("test");
        var restoredError = finalizer.Invoke(null, [null, controls, pos, 11L, state, error]);
        if (!flags[4] || !ReferenceEquals(error, restoredError) || (double)motionY.GetValue(motion)! != -.2) throw new Exception("Exception cleanup changed falling motion.");
        motionY.SetValue(motion, .6d);
        finalizer.Invoke(null, [null, controls, pos, 10L, state, null]);
        if ((double)motionY.GetValue(motion)! != .6) throw new Exception("Non-jump motion was scaled.");
        Console.WriteLine("PASS jump scaling, falling motion and exception-safe control cleanup");
        var claim = bridgeType.GetMethod("ClaimHitCycle", BindingFlags.Static | BindingFlags.NonPublic)!;
        var manager = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(claim.GetParameters()[0].ParameterType);
        if (!(bool)claim.Invoke(null, [manager])!
            || (bool)claim.Invoke(null, [manager])!) throw new Exception("Native attack cycle replay charged twice.");
        bridgeType.GetMethod("StopHitCycle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [manager, null]);
        if (!(bool)claim.Invoke(null, [manager])!) throw new Exception("Next native attack cycle remained latched.");
        Console.WriteLine("PASS native attack cycle replay deduplication and next-cycle reset");
        var mild = Activator.CreateInstance(stateType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [true, 10L, .9d], null)!;
        motionY.SetValue(motion, 1d);
        finalizer.Invoke(null, [null, controls, pos, 11L, mild, null]);
        if ((double)motionY.GetValue(motion)! != 1) throw new Exception("Diet/fatigue removed native one-block jump clearance.");

        // Exercise the actual server adapter with body and view facing opposite directions.
        var playerType = api.GetType("Vintagestory.API.Common.EntityPlayer", true)!;
        var player = Activator.CreateInstance(playerType)!;
        playerType.GetMethod("SetSelectionBox")!.Invoke(player, [.6f, 1.8f]);
        playerType.GetProperty("BodyYawServer")!.SetValue(player, 0f);
        var playerPos = playerType.GetProperty("Pos")!.GetValue(player)!;
        posType.GetProperty("Yaw")!.SetValue(playerPos, (float)Math.PI);
        var damageType = api.GetType("Vintagestory.API.Common.DamageSource", true)!;
        var damage = Activator.CreateInstance(damageType)!;
        var kindField = damageType.GetField("Type")!;
        kindField.SetValue(damage, Enum.Parse(kindField.FieldType, "SlashingAttack"));
        var vectorType = api.GetType("Vintagestory.API.MathTools.Vec3d", true)!;
        damageType.GetField("HitPosition")!.SetValue(damage, Activator.CreateInstance(vectorType, [-.29d, .9d, 0d]));
        var resolveHit = mod.GetType("Seraph.Server.ServerCoordinator", true)!
            .GetMethod("ResolveHitRegion", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Region(object? source) {
            var result = resolveHit.Invoke(null, [player, source])!;
            return (string)result.GetType().GetProperty("Region")!.GetValue(result)!;
        }
        if (Region(damage) != "left-hand") throw new Exception("Server hit adapter used view yaw instead of body yaw.");
        playerType.GetField("Swimming")!.SetValue(player, true);
        if (Region(damage) != "abdomen" || Region(null) != "general") throw new Exception("Horizontal pose missed its explicit estimated region or systemic source acquired a region.");
        Console.WriteLine("PASS server hit adapter uses body orientation and estimates uncertain horizontal attacks without upright geometry");
    }
    using (var labor = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeLaborBridge", true)!, nonPublic: true)!)
        Console.WriteLine("PASS native labor hooks install on 1.22.7");
    var harmony = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony")!;
    Assembly.Load("VintagestoryLib");
    var calendarType = mod.GetType("Seraph.Server.NativeCalendarBridge", true)!;
    var coordinator = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(mod.GetType("Seraph.Server.ServerCoordinator", true)!);
    using (var calendar = (IDisposable)Activator.CreateInstance(calendarType,
        BindingFlags.Instance | BindingFlags.NonPublic, null, [coordinator], null)!)
        Console.WriteLine("PASS native calendar command and sleep observation hooks install on 1.22.7");
    foreach (var name in new[] { "Seraph.Server.NativePhysiologyBridge", "Seraph.Server.NativeBandageBridge", "Seraph.Client.NativeFoodTooltips", "Seraph.Server.NativeFoodBridge", "Seraph.World.NativeWaterHeating", "Seraph.World.NativeCropPollution", "Seraph.World.NativeAshGeneration" })
    {
        var type = mod.GetType(name, true)!;
        using var bridge = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        var patched = (IEnumerable<MethodBase>)harmony.GetMethod("GetAllPatchedMethods")!.Invoke(null, null)!;
        if (!patched.Any(m => m.DeclaringType?.Assembly.GetName().Name is "VSSurvivalMod" or "VSEssentials" or "VintagestoryAPI"))
            throw new Exception("Missing clinical hooks: " + name);
        Console.WriteLine("PASS real game patch installation: " + name);
    }
    SymptomChecks.Run(mod);
    SleepEyeCombatChecks.Run(mod);
    RuntimeDiagnosticsChecks.Run(mod);
    MotorChecks.Run(mod);
    SurfaceWaterBridgeChecks.Run();
    WaterCompatibilityBridgeChecks.Run(mod);
    DistillationGameChecks.Run();
    WaterInfoChecks.Run(mod);
    WaterHoverChecks.Run();
    FixtureHelpChecks.Run();
    PollutionCaptureChecks.Run(mod);
    MapHoverRenderChecks.Run();
    FixtureChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")));
    FixtureOrientationChecks.Run();
    ClosedEyeRestChecks.Run(gamePath);
    BedRestChecks.Run();
    CharacterPanelRenderChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    BodyFunctionPanelChecks.Run(mod, args.Skip(1).FirstOrDefault(Directory.Exists));
    LatrineChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), gamePath);
    FixtureWaterChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")));
    TreatmentChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), gamePath);
    BathActionChecks.Run();
    BiteChecks.Run(mod);
    PressureChecks.Run();
    BleedingChecks.Run(mod);
    NativeLaborExecutionChecks.Run(mod);
    CharacterMetricChecks.Run(mod);
    ClinicalWarningBridgeChecks.Run(mod);
    MedicineUseChecks.Run();
    GroundDrinkChecks.Run();
    GroundwaterParticleChecks.Run(mod);
    WellStructureChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), gamePath);
    RecipeChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")));
    ChimneyChecks.Run();
    DebugValueChecks.Run();
    FoodChecks.Run(mod);
    WaterHeatingGameChecks.Run();
    ClothingChecks.Run();
    WaterImmersionChecks.Run();
    using ((IDisposable)Activator.CreateInstance(mod.GetType("Seraph.World.NativeManureFertilizer", true)!, nonPublic: true)!)
        Console.WriteLine("PASS installed mass-scaled native fertilizer inputs");
    using ((IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeSanitationBridge", true)!,
        BindingFlags.Instance | BindingFlags.NonPublic, null, [coordinator], null)!)
        Console.WriteLine("PASS installed native butchering sanitation hook on 1.22.7");
    var remaining = (IEnumerable<MethodBase>)harmony.GetMethod("GetAllPatchedMethods")!.Invoke(null, null)!;
    // Harmony may retain method entries after their last patch is removed. Inspect active owners.
    var getPatchInfo = harmony.GetMethod("GetPatchInfo")!;
    if (remaining.Any(method => getPatchInfo.Invoke(null, [method]) is { } info
        && ((IEnumerable<string>)info.GetType().GetProperty("Owners")!.GetValue(info)!)
            .Any(o => o.StartsWith("vsslssmod.", StringComparison.Ordinal))))
        throw new Exception("Owned patches were left installed after disposal.");
    Console.WriteLine("PASS owned Harmony patches removed on disposal");
    CheckManureStorage(mod, Assembly.Load("VSSurvivalMod"), natural: false);
    if (args.Skip(1).FirstOrDefault(File.Exists) is { } manureAssembly) CheckManureStorage(mod, AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(manureAssembly)), natural: true);
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

static void CheckManureStorage(Assembly mod, Assembly storageAssembly, bool natural)
{
    var ns = natural ? "naturalfertilizer." : "Vintagestory.GameContent.";
    var domain = natural ? "naturalfertilizer" : "vsslssmod";
    var propsName = natural ? "ManureStackProps" : "StorageProps";
    var capacity = natural ? 32 : 12;
    Type StorageType(string type) => storageAssembly.GetType(ns + type, true)!;
    var api = Assembly.Load("VintagestoryAPI");
    Type Api(string name) => api.GetType("Vintagestory.API.Common." + name, true)!;
    object New(string name) => Activator.CreateInstance(Api(name))!;
    void Field(object obj, string name, object? value) {
        if (obj.GetType().GetField(name) is { } field) field.SetValue(obj, value);
        else obj.GetType().GetProperty(name)!.SetValue(obj, value);
    }
    object Location(string code) => Activator.CreateInstance(Api("AssetLocation"), [code])!;
    var manure = New("Item"); Field(manure, "ItemId", 100); Field(manure, "Code", Location(domain + ":manure"));
    Field(manure, "MaxStackSize", 64);
    var behavior = Activator.CreateInstance(StorageType(natural ? "CollectibleBehaviorManureStack" : "CollectibleBehaviorGroundStorable"), [manure])!;
    var props = Activator.CreateInstance(StorageType(natural ? "ManureStackProperties" : "GroundStorageProperties"))!;
    if (natural) Field(props, "StackingCapacity", capacity);
    else { var layout = props.GetType().GetField("Layout")!; layout.SetValue(props, Enum.Parse(layout.FieldType, "Messy12")); }
    behavior.GetType().GetProperty(propsName)!.SetValue(behavior, props);
    var behaviors = Array.CreateInstance(Api("CollectibleBehavior"), 1); behaviors.SetValue(behavior, 0);
    Field(manure, "CollectibleBehaviors", behaviors);
    var storage = Activator.CreateInstance(StorageType(natural ? "BlockManureStack" : "BlockGroundStorage"))!;
    Field(storage, "BlockId", 42); Field(storage, "Code", Location(natural ? "naturalfertilizer:manurestack" : "game:groundstorage"));
    var entity = Activator.CreateInstance(StorageType(natural ? "BlockEntityManureStack" : "BlockEntityGroundStorage"))!;
    var inventory = entity.GetType().GetProperty("Inventory")!.GetValue(entity)!;
    object Slot(int index) => inventory.GetType().GetProperty("Item")!.GetValue(inventory, [index])!;
    void Put(int index, object? item, int count = 1) => Slot(index).GetType().GetProperty("Itemstack")!.SetValue(Slot(index),
        item is null ? null : Activator.CreateInstance(Api("ItemStack"), [item, count]));
    object current = New("Block");
    var failDirty = false;
    object? Dirty() { if (failDirty) { failDirty = false; throw new InvalidOperationException("Injected dirty failure"); } return null; }
    var accessor = RoutingProxy.Make(Api("IBlockAccessor"), (method, _) => method.Name switch {
        "GetBlock" => current, "GetBlockEntity" => entity, "MarkBlockEntityDirty" => Dirty(), "MarkBlockDirty" => null,
        _ => throw new Exception("Unexpected accessor call " + method.Name)
    });
    var world = RoutingProxy.Make(Api("IWorldAccessor"), (method, _) => method.Name switch {
        "GetItem" => manure, "GetBlock" => storage, "get_BlockAccessor" => accessor,
        _ => throw new Exception("Unexpected world call " + method.Name)
    });
    var adapterType = mod.GetType("Seraph.World.ManureGroundStorage", true)!;
    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    var adapter = adapterType.GetMethod(natural ? "Resolve" : "ResolveFallback", flags)!.Invoke(null, [world]) ?? throw new Exception("NF API contract unavailable.");
    if ((int)adapterType.GetProperty("Capacity", flags)!.GetValue(adapter)! != capacity) throw new Exception("Storage capacity does not match its native layout.");
    var pos = Activator.CreateInstance(api.GetType("Vintagestory.API.MathTools.BlockPos", true)!, [0, 0, 0, 0])!;
    void Read(bool expected, int quantity) {
        object?[] values = [world, pos, 0];
        var actual = (bool)adapterType.GetMethod("TryRead", flags)!.Invoke(adapter, values)!;
        if (actual != expected || actual && (int)values[2]! != quantity) throw new Exception("NF storage validation mismatch.");
    }
    Read(true, 0); // Empty ground.
    current = storage; Put(0, manure, 4); Read(true, 4);
    try {
        adapterType.GetMethod("Add", flags)!.Invoke(adapter, [world, pos, 42, 3, 1, null, null]);
        throw new Exception("Stale quantity was accepted.");
    } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
    Read(true, 4); // Interference must never overwrite another player's contents.
    adapterType.GetMethod("Add", flags)!.Invoke(adapter, [world, pos, 42, 4, 2, null, null]);
    Read(true, 6);
    if (!ReferenceEquals(props, entity.GetType().GetProperty(propsName)!.GetValue(entity)))
        throw new Exception("Native storage properties not initialized from manure item.");
    var coreApi = RoutingProxy.Make(Api("ICoreAPI"), (method, _) => method.Name == "get_World" ? world
        : throw new Exception("Unexpected core API call " + method.Name));
    Field(entity, "Api", coreApi); failDirty = true;
    try {
        adapterType.GetMethod("Add", flags)!.Invoke(adapter, [world, pos, 42, 6, 2, null, null]);
        throw new Exception("Injected mutation failure was swallowed.");
    } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
    Read(true, 6); // Preserve all previously placed items when a commit fails.
    Field(entity, "Api", null);
    Put(0, manure, capacity); Read(true, capacity);
    Put(0, manure, capacity + 1); Read(false, 0);
    Put(0, New("Item")); Read(false, 0);
    Put(0, manure); Put(1, manure); Read(false, 0);
    Put(0, null); Put(1, null); Read(true, 0);
    var contains = adapterType.GetMethod("ContainsManure", flags)!;
    if ((bool)contains.Invoke(null, [entity])!) throw new Exception("Empty NF stack treated as fecal pollution.");
    Put(0, manure);
    if (!(bool)contains.Invoke(null, [entity])!) throw new Exception("NF manure pollution not detected.");
    Console.WriteLine($"PASS {(natural ? "Natural Fertilizer" : "vanilla Messy12")} API, item addition, capacity, foreign/mixed contents, stale-action rejection, rollback and pollution");
}

public class RoutingProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> handler = null!;
    public static object Make(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = Create(type, typeof(RoutingProxy));
        ((RoutingProxy)proxy).handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!, args);
}

