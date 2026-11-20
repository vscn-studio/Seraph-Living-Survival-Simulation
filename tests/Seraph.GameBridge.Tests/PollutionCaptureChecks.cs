using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Runtime.CompilerServices;
using Seraph.Application;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Seraph.World;

internal static class PollutionCaptureChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var type = mod.GetType("Seraph.World.ChunkPollutionStore", true)!;
        var columnType = type.GetNestedType("Column", flags)!;
        var scanType = type.GetNestedType("Scan", flags)!;
        var keyType = mod.GetType("Seraph.World.PollutionChunk", true)!;
        object Key(int dimension = 0) => Activator.CreateInstance(keyType, [0, 0, dimension])!;
        object? Field(object instance, string name) => instance.GetType().GetField(name, flags)!.GetValue(instance);
        object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, flags)!.Invoke(instance, args);
        object Proxy(Type contract, System.Func<MethodInfo, object?[]?, object?> route) => RoutingProxy.Make(contract, route);
        var blocks = new List<Block> {
            new() { BlockId = 0, Code = new("game:air") },
            new() { BlockId = 1, Code = new("vsslssmod:smallpoop-fresh") },
            new() { BlockId = 2, Code = new("game:leaves-oak"), BlockMaterial = EnumBlockMaterial.Leaves },
            new() { BlockId = 3, Code = new("game:soil-medium-none") },
            new() { BlockId = 4, Code = new("game:water-still-7"), LiquidCode = "water" }
        };
        var reads = 0; var disposed = false; var hours = 10d; var now = 1000L;
        var persisted = new Dictionary<string, byte[]>();
        var map = Proxy(typeof(IMapChunk), (m, _) => m.Name == "get_RainHeightMap" ? Enumerable.Repeat((ushort)21, 1024).ToArray()
            : throw new Exception("Unexpected map call " + m.Name));
        var data = Proxy(typeof(IWorldChunk).GetProperty("Data")!.PropertyType, (m, a) => {
            if (m.Name == "FuzzyListBlockIds") { ((List<int>)a![0]!).AddRange([0, 1, 2, 3]); return null; }
            if (m.Name == "GetFluid") { reads++; return 4; }
            if (m.Name == "GetBlockId") {
                reads++; var index = (int)a![0]!; var y = index / 1024;
                return index == 5 + 32 * (6 + 32 * 3) ? 1 : y == 20 ? 2 : y == 10 ? 3 : 0;
            }
            throw new Exception("Unexpected chunk data call " + m.Name);
        });
        Entity[] organicEntities = [];
        Dictionary<BlockPos, BlockEntity>? organicContainers = null;
        var chunk = Proxy(typeof(IServerChunk), (m, a) => m.Name switch {
            "get_Disposed" => disposed, "get_Data" => data, "get_MapChunk" => map,
            // Native generated chunks can expose null entity containers before preparation.
            "Unpack_ReadOnly" => true, "get_BlockEntities" => organicContainers,
            "get_Entities" => organicEntities.Length == 0 ? null : organicEntities, "get_EntitiesCount" => organicEntities.Length,
            "GetModdata" => persisted.GetValueOrDefault((string)a![0]!),
            "SetModdata" => Save((string)a![0]!, (byte[])a![1]!), "MarkModified" => null,
            _ => throw new Exception("Unexpected chunk call " + m.Name)
        });
        object? Save(string key, byte[] value) { persisted[key] = value; return null; }
        var accessor = Proxy(typeof(IBlockAccessor), (m, _) => m.Name switch {
            "GetTerrainMapheightAt" => 10, "GetClimateAt" => new ClimateCondition { Temperature = 20, Rainfall = .5f },
            "GetWindSpeedAt" => new Vec3d(1, 0, 0),
            _ => throw new Exception("Unexpected environment call " + m.Name)
        });
        var calendar = Proxy(typeof(IGameCalendar), (m, _) => m.Name == "get_HoursPerDay" ? (object)24f : m.Name == "get_TotalHours" ? hours
            : throw new Exception("Unexpected calendar call " + m.Name));
        var world = Proxy(typeof(IServerWorldAccessor), (m, _) => m.Name switch {
            "get_Blocks" => blocks, "get_BlockAccessor" => accessor, "get_Calendar" => calendar, "get_ElapsedMilliseconds" => now,
            _ => throw new Exception("Unexpected world call " + m.Name)
        });
        var loaded = new Dictionary<long, IServerChunk> { [0] = (IServerChunk)chunk };
        var manager = Proxy(typeof(IWorldManagerAPI), (m, a) => m.Name switch {
            "get_AllLoadedChunks" => loaded,
            "ChunkIndex3D" => (long)(int)a![0]! + (long)(int)a[2]! * 1024 + (long)(int)a[1]! * 1024 * 1024,
            _ => throw new Exception("Unexpected world manager call " + m.Name)
        });
        var hooks = new Dictionary<string, Delegate>();
        var events = Proxy(typeof(IServerEventAPI), (m, a) => {
            if (m.Name.StartsWith("add_")) { hooks[m.Name[4..]] = (Delegate)a![0]!; return null; }
            if (m.Name.StartsWith("remove_")) { hooks.Remove(m.Name[7..]); return null; }
            return m.Name switch {
                "RegisterGameTickListener" => 1L, "UnregisterGameTickListener" => null,
                _ => throw new Exception("Unexpected event call " + m.Name)
            };
        });
        var api = Proxy(typeof(ICoreServerAPI), (m, _) => m.Name switch {
            "get_Logger" => Proxy(typeof(ILogger), (_, _) => null),
            "get_World" => world, "get_Event" => events, "get_WorldManager" => manager, "get_Side" => EnumAppSide.Server,
            _ => throw new Exception("Unexpected core API call " + m.Name)
        });
        using var store = (IDisposable)Activator.CreateInstance(type, flags, null,
            [api, (Func<SeraphServerConfig>)(() => new())], null)!;
        var key = Key(); var column = Activator.CreateInstance(columnType, true)!;
        ((IDictionary)Field(column, "Chunks")!)[0] = chunk;
        ((IDictionary)Field(store, "columns")!)[key] = column;
        Call(store, "ClassifyBlocks");
        var scan = Activator.CreateInstance(scanType, flags, null, [key, column, hours, new SeraphServerConfig()], null)!;
        var done = false;
        for (var batch = 0; batch < 1000 && !done; batch++) {
            var before = reads; object?[] args = [scan, 8192];
            done = (bool)type.GetMethod("Step", flags)!.Invoke(store, args)!;
            Require(reads - before <= 512 && (int)args[1]! >= 0, "Capture exceeded the per-batch world-read budget.");
        }
        Require(done, "A real chunk scan never completed.");
        var organisms = (IDictionary)Field(store, "animalTypes")!;
        organisms[10] = (true, true);
        EntityAgent Organism(string code, bool alive, long id)
        {
            var e = new EntityAgent { EntityId = id, Code = new(code), Alive = alive };
            typeof(Entity).GetProperty("Properties")!.SetValue(e, new EntityProperties { Id = 10, Weight = 100 });
            return e;
        }
        var rot = new Item { Code = new("game:rot"), ItemId = 100 };
        organicEntities = [Organism("game:drifter-normal", true, 1), Organism("game:shiver-normal", false, 2),
            Organism("game:locust-bronze", false, 3), Organism("game:wolf-male", true, 4),
            Organism("game:pig-female", false, 5),
            new EntityPlayer { Code = new("game:player"), Alive = true, EntityId = 6 },
            new EntityPlayer { Code = new("game:player"), Alive = false, EntityId = 7 },
            new EntityAgent { Code = new("dead:playercorpse"), Alive = true, EntityId = 8 },
            new EntityItem { Code = new("game:item"), EntityId = 9, Itemstack = new ItemStack(rot, 8) }];
        var organicScan = Activator.CreateInstance(scanType, flags, null, [key, column, hours, new SeraphServerConfig()], null)!;
        object?[] organicArgs = [organicScan, chunk, 64];
        Require((bool)type.GetMethod("CaptureEntities", flags)!.Invoke(store, organicArgs)!, "Organic scan failed to finish.");
        var organicLoad = (PollutionLoad)Field(organicScan, "Load")!;
        Require((int)Field(organicScan, "Carrion")! == 4 && organicLoad.Carrion == 4
            && organicLoad.Animals == 1 && ((IDictionary)Field(organicScan, "Animals")!).Count == 1,
            "Fantasy/living players counted or biological remains/rot ignored.");
        Require((int)Field(organicScan, "SourceIds")!.GetType().GetProperty("Count")!.GetValue(Field(organicScan, "SourceIds"))! == 5,
            "Distinct organic sources sharing a block were merged or fantasy sources counted.");
        organicEntities = [];
        Console.WriteLine("PASS organic capture excludes fantasy entities, retains wolves, animal/player/Dead corpses and dropped rot");
        var carcass = new Item { Code = new("butchering:deadpig-male-eurasian-normal-1-rotten"), ItemId = 101,
            Attributes = Vintagestory.API.Datastructures.JsonObject.FromJson("{\"bloodType\":\"butchering:bloodportion\"}") };
        var corpseStack = new ItemStack(carcass);
        Require(OrganicPollutionSource.Kind(corpseStack) == OrganicWasteKind.Corpse
            && OrganicPollutionSource.DecayQuantity((IWorldAccessor)world, corpseStack) == 8,
            "Butchering's actual rotten carcass asset lost its corpse identity or decay mass.");
        Require(!OrganicPollutionSource.IsRot(new ItemStack(new Item { Code = new("game:wood-rotten") }))
            && !OrganicPollutionSource.IsPlayerCorpse(new("machines:corpse-robot")),
            "Rotten wood or an unrelated corpse name became biological waste.");
        organicEntities = [new EntityItem { Code = new("game:item"), EntityId = 11, Itemstack = corpseStack }];
        var carcassScan = Activator.CreateInstance(scanType, flags, null, [key, column, hours, new SeraphServerConfig()], null)!;
        object?[] carcassArgs = [carcassScan, chunk, 64];
        type.GetMethod("CaptureEntities", flags)!.Invoke(store, carcassArgs);
        Require(((PollutionLoad)Field(carcassScan, "Load")!).Carrion == 1, "Dropped rotten carcass did not become a pollution source.");
        organicEntities = [];
        // Ground storage and Butchering workstations inherit BlockEntityContainer.
        var ground = new BlockEntityGroundStorage { Pos = new(2, 3, 2, 0) };
        ground.Inventory[0].Itemstack = corpseStack;
        organicContainers = new() { [ground.Pos] = ground };
        var groundScan = Activator.CreateInstance(scanType, flags, null, [key, column, hours, new SeraphServerConfig()], null)!;
        object?[] groundArgs = [groundScan, chunk, 64];
        type.GetMethod("CaptureEntities", flags)!.Invoke(store, groundArgs);
        Require(((PollutionLoad)Field(groundScan, "Load")!).Carrion == 1, "Ground-stored rotten carcass was ignored.");
        var rottenBarrel = new BlockEntityBarrel { Pos = new BlockPos(2, 3, 2, 0) };
        rottenBarrel.Inventory[0].Itemstack = new ItemStack(rot, 16);
        organicContainers = new() { [rottenBarrel.Pos] = rottenBarrel };
        foreach (var sealedBarrel in new[] { false, true })
        {
            rottenBarrel.Sealed = sealedBarrel;
            var containerScan = Activator.CreateInstance(scanType, flags, null, [key, column, hours, new SeraphServerConfig()], null)!;
            object?[] containerArgs = [containerScan, chunk, 64];
            type.GetMethod("CaptureEntities", flags)!.Invoke(store, containerArgs);
            Require(((PollutionLoad)Field(containerScan, "Load")!).Carrion == (sealedBarrel ? 0 : 2),
                "Rotten contents ignored or sealed barrel did not attenuate pollution.");
            if (sealedBarrel)
            {
                columnType.GetField("SealedSources", flags)!.SetValue(column, Field(containerScan, "SealedSources"));
                var beforeDebug = reads;
                var markers = ((ChunkPollutionStore)store).DebugVolumes(rottenBarrel.Pos);
                Require(markers.Length == 1 && markers[0].Kind == Seraph.Network.PollutionDebugKind.SealedSource
                    && !((ChunkPollutionStore)store).OrganicGasSources().Any() && reads == beforeDebug,
                    "Sealed debug marker missing, emitted gas or scanned blocks.");
            }
        }
        organicContainers = null;
        WaterQuality.BindPollution((IWorldAccessor)world, (ChunkPollutionStore)store);
        var revision = (long)Field(column, "Revision")!;
        var droppedRot = new EntityItem { Code = new("game:item"), Itemstack = new ItemStack(rot), EntityId = 12 };
        hooks["OnEntitySpawn"].DynamicInvoke(droppedRot);
        hooks["OnEntityDespawn"].DynamicInvoke(droppedRot, new EntityDespawnData());
        hooks["OnEntityDeath"].DynamicInvoke(Organism("game:pig-male", false, 13), new DamageSource());
        Require((long)Field(column, "Revision")! == revision + 3,
            "Source creation/removal/death did not invalidate the cached column immediately.");
        Call(store, "Finish", scan, now);
        var pos = new BlockPos(16, 15, 16, 0);
        var sample = Call(store, "Get", pos)!;
        Require((int)sample.GetType().GetProperty("Manure")!.GetValue(sample)! == 1, "Exact manure count used the wrong chunk index.");
        Require((int)sample.GetType().GetProperty("SourceCount")!.GetValue(sample)! == 1, "Cached source count differs from actual sources.");
        var sources = (RegionalSources)sample.GetType().GetProperty("Sources")!.GetValue(sample)!;
        Require(sources.Forest == 1, "Actual rain-height sampling did not see the tree canopy.");
        var routes = Field(store, "routes")!;
        for (var i = 0; i < 500; i++) {
            object?[] routeArgs = [512, System.Diagnostics.Stopwatch.StartNew()];
            Call(routes, "Advance", routeArgs);
        }
        var beforeLookup = reads;
        var debugVolumes = ((ChunkPollutionStore)store).DebugVolumes(new(5, 3, 6, 0));
        Require(debugVolumes.Any(p => p.Kind == Seraph.Network.PollutionDebugKind.Source) && debugVolumes.Length <= 768
            && ((ChunkPollutionStore)store).DebugVolumes(new(5, 3, 6, 1)).Length == 0 && reads == beforeLookup,
            "Debug inspection scanned blocks, lost its source or crossed dimension/budget.");
        var load = (PollutionLoad)Call(store, "LoadAt", new BlockPos(6, 2, 6, 0))!;
        Require(load.Fecal > 0 && reads == beforeLookup, "Nearby downhill water did not use its cached point source.");
        Require(((PollutionLoad)Call(store, "LoadAt", new BlockPos(5, 4, 6, 0))!).Fecal == 0,
            "Fecal contamination travelled uphill into a raised well.");
        Require(((PollutionLoad)Call(store, "LoadAt", new BlockPos(14, 2, 6, 0))!).Fecal == 0,
            "Fecal contamination exceeded its eight-block local radius.");
        Require(((PollutionLoad)Call(store, "LoadAt", pos)!).Fecal == 0,
            "A distant source contaminated the entire map column.");
        Require(Call(store, "Get", new BlockPos(16, 15, 16, 1)) is null, "Pollution crossed dimensions.");
        var mapSampleType = mod.GetType("Seraph.World.PollutionMapLayer", true)!.GetNestedType("MapSample", flags)!;
        var mapSample = Activator.CreateInstance(mapSampleType, flags, null, [sample, 2d, .02d, .25d], null)!;
        var wire = JsonSerializer.SerializeToUtf8Bytes(mapSample, mapSampleType);
        var restoredSample = JsonSerializer.Deserialize(wire, mapSampleType)!;
        var restoredSnapshot = mapSampleType.GetProperty("Snapshot")!.GetValue(restoredSample)!;
        Require((int)restoredSnapshot.GetType().GetProperty("Manure")!.GetValue(restoredSnapshot)! == 1
            && (int)restoredSnapshot.GetType().GetProperty("SourceCount")!.GetValue(restoredSnapshot)! == 1
            && ((PollutionLoad)restoredSnapshot.GetType().GetProperty("Load")!.GetValue(restoredSnapshot)!).Fecal == 1,
            "Map serialization lost source values.");
        Require(((RegionalEnvironment)restoredSnapshot.GetType().GetProperty("Region")!.GetValue(restoredSnapshot)!).Valid(),
            "The real map payload did not survive JSON roundtrip.");
        // Exercise the real receive -> main-thread installation -> native map hover path.
        var clientEntity = new EntityPlayer();
        var clientPlayer = FixtureClientPlayer.Make(clientEntity);
        var clientWorld = Proxy(typeof(IClientWorldAccessor), (m, _) => m.Name switch {
            "get_Player" => clientPlayer, "get_ElapsedMilliseconds" => now,
            _ => throw new Exception("Unexpected map world call " + m.Name)
        });
        var clientEvents = Proxy(typeof(IClientEventAPI), (m, a) => {
            if (m.Name == "EnqueueMainThreadTask") { ((Action)a![0]!)(); return null; }
            throw new Exception("Unexpected map event " + m.Name);
        });
        var logger = Proxy(typeof(ILogger), (_, _) => null);
        var draws = new List<(double X, double Y, double Width, double Height)>();
        var mouseX = 160; var mouseY = 160;
        var input = Proxy(typeof(IInputAPI), (m, _) => m.Name switch {
            "get_MouseX" => mouseX, "get_MouseY" => mouseY, _ => throw new Exception(m.Name)
        });
        var render = Proxy(typeof(IRenderAPI), (m, a) => {
            if (m.Name == "Render2DTexture") { draws.Add((Convert.ToDouble(a![1]), Convert.ToDouble(a[2]), Convert.ToDouble(a[3]), Convert.ToDouble(a[4]))); return null; }
            if (m.Name == "DeleteTexture") return null;
            throw new Exception("Unexpected map render " + m.Name);
        });
        var clientApi = Proxy(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_World" => clientWorld, "get_Event" => clientEvents, "get_Logger" => logger,
            "get_Render" => render, "get_Input" => input, "get_Gui" => Proxy(typeof(IGuiAPI), (m, _) => m.Name == "DeleteTexture" ? null : throw new Exception(m.Name)),
            _ => throw new Exception("Unexpected map API " + m.Name)
        });
        var mapLayer = new PollutionMapLayer((ICoreClientAPI)clientApi, null!);
        var mapArray = Array.CreateInstance(mapSampleType, 1); mapArray.SetValue(mapSample, 0);
        mapLayer.OnDataFromServer(JsonSerializer.SerializeToUtf8Bytes(mapArray, mapArray.GetType()));
        Require(((IDictionary)Field(mapLayer, "cells")!).Count == 1, "Client silently rejected the real map payload.");
        RuntimeEnv.GUIScale = 1;
        var translations = new TranslationService("zh-cn", null!);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsslssmod/lang/zh-cn.json")))!;
        typeof(TranslationService).GetField("entryCache", flags)!.SetValue(translations, entries.ToDictionary(p => "vsslssmod:" + p.Key, p => p.Value));
        typeof(TranslationService).GetField("loaded", flags)!.SetValue(translations, true);
        Lang.AvailableLanguages["zh-cn"] = translations; Lang.ChangeLanguage("zh-cn");
        var mapElement = (GuiElementMap)RuntimeHelpers.GetUninitializedObject(typeof(GuiElementMap));
        mapElement.Bounds = ElementBounds.Fixed(0, 0, 320, 320).WithParent(ElementBounds.Empty);
        mapElement.Bounds.CalcWorldBounds();
        mapElement.CurrentBlockViewBounds = new Cuboidd(0, 0, 0, 32, 1, 32);
        var hover = new StringBuilder();
        mapLayer.OnMouseMoveClient(new MouseEvent(160, 160), mapElement, hover);
        Require(hover.ToString().Contains("污染源：1") && !hover.ToString().Contains("粪便方块/储存"),
            "Native map hover lost actual sampled values/source counts: " + hover);
        var snapshotType = sample.GetType();
        var salineSnapshot = JsonSerializer.Deserialize(JsonSerializer.Serialize(sample, snapshotType), snapshotType)!;
        snapshotType.GetProperty("SaltWater")!.SetValue(salineSnapshot, 1);
        var salineMap = Activator.CreateInstance(mapSampleType, flags, null, [salineSnapshot, 2d, 0d, .25d], null)!;
        // The production map sample carries the sampled water batch separately
        // from the pollution snapshot. Populate it here so the hover path tests
        // the same saline warning shown for a real sampled water cell.
        mapSampleType.GetProperty("Water")!.SetValue(salineMap, new WaterBatch
        {
            VolumeL = 1000,
            SaltGramsPerL = 35,
            Source = "salt",
            Unverified = false
        });
        mapArray.SetValue(salineMap, 0);
        mapLayer.OnDataFromServer(JsonSerializer.SerializeToUtf8Bytes(mapArray, mapArray.GetType()));
        hover.Clear(); mapLayer.OnMouseMoveClient(new MouseEvent(160, 160), mapElement, hover);
        Require(hover.ToString().Contains("不要饮用") && !hover.ToString().Contains("可直接饮用"),
            "Map advice treated a saline sample as unsalted default water.");
        mapElement.CurrentBlockViewBounds = new Cuboidd(320, 0, 320, 352, 1, 352);
        hover.Clear(); mapLayer.OnMouseMoveClient(new MouseEvent(160, 160), mapElement, hover);
        Require(hover.ToString().Contains("暂无采样"), "Unsampled map column silently produced no information.");
        var cachedText = Field(mapLayer, "hoverText");
        hover.Clear(); mapLayer.OnMouseMoveClient(new MouseEvent(161, 161), mapElement, hover);
        Require(ReferenceEquals(cachedText, Field(mapLayer, "hoverText")), "Same-column hover rebuilt its cached text.");
        hover.Clear(); mapLayer.OnMouseMoveClient(new MouseEvent(-1, 160), mapElement, hover);
        Require(hover.Length == 0, "Map hover leaked outside its viewport.");
        typeof(PollutionMapLayer).GetField("pixel", flags)!.SetValue(mapLayer, new LoadedTexture((ICoreClientAPI)clientApi, 123, 1, 1));
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        {
            RuntimeEnv.GUIScale = scale;
            mapElement.Bounds = ElementBounds.Fixed(0, 0, 320, 320).WithEmptyParent(); mapElement.Bounds.CalcWorldBounds();
            mapElement.CurrentBlockViewBounds = new Cuboidd(-16, 0, -16, 80, 1, 80);
            draws.Clear(); mapLayer.Render(mapElement, 0);
            Require(draws.Count == 5 && draws.Count(d => d.Width == 1) == 2 && draws.Count(d => d.Height == 1) == 2,
                "Map did not draw the sampled cell plus its four borders at scale " + scale);
            var fill = draws.Single(d => d.Width > 1 && d.Height > 1);
            Require(Math.Abs(fill.Width - 320 * scale / 3) < .001 && Math.Abs(fill.Height - fill.Width) < .001,
                "Sample overlay is still split into half-cell bars.");
            Require(draws.All(d => d.X >= 0 && d.Y >= 0), "Negative chunk coordinates escaped viewport conversion.");
        }
        mapLayer.OnDataFromServer("[]"u8.ToArray()); draws.Clear(); mapLayer.Render(mapElement, 0);
        Require(draws.Count == 0, "Empty sample set still rendered a grid.");
        mapLayer.Active = false; draws.Clear(); mapLayer.Render(mapElement, 0);
        Require(draws.Count == 0, "Disabled layer still rendered.");
        RuntimeEnv.GUIScale = 1;
        Console.WriteLine("PASS map sampled-cell fills and borders only at three scales, cached hover, pointer bounds and layer disable");
        mapLayer.Dispose();
        // Dispatch through the real manager/composer and a recording native dialog.
        // GPU tooltip composition remains part of the separate native Cairo tests.
        var dialog = (RecordingMapDialog)RuntimeHelpers.GetUninitializedObject(typeof(RecordingMapDialog));
        dialog.Composers = new GuiDialog.DlgComposers(dialog);
        var composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), flags, null,
            [clientApi, mapElement.Bounds, "map-pollution-check"], null)!;
        ((IDictionary)typeof(GuiComposer).GetField("interactiveElements", flags)!.GetValue(composer)!)["mapElem"] = mapElement;
        dialog.SingleComposer = composer; dialog.SetState(true, true, EnumDialogType.Dialog);
        var nativeManager = (WorldMapManager)RuntimeHelpers.GetUninitializedObject(typeof(WorldMapManager));
        nativeManager.worldMapDlg = dialog;
        var polling = new PollutionMapLayer((ICoreClientAPI)clientApi, nativeManager);
        polling.OnTick(0); Require(dialog.Refreshes == 1, "Focused map did not refresh stationary hover.");
        for (var i = 0; i < 60; i++) polling.OnTick(.016f);
        Require(dialog.Refreshes == 1, "Unchanged map rebuilt hover every frame.");
        mapElement.CurrentBlockViewBounds.X1++;
        polling.OnTick(0); Require(dialog.Refreshes == 1, "Panning bypassed synthetic Cairo refresh throttle.");
        now += 200; polling.OnTick(0); Require(dialog.Refreshes == 2, "Panning under stationary pointer did not refresh hover.");
        now += 1000; polling.OnTick(0); Require(dialog.Refreshes == 3, "Sample age did not refresh once per second.");
        polling.OnDataFromServer("[]"u8.ToArray()); Require(dialog.Refreshes == 4, "Incoming data did not refresh native hover.");
        dialog.SetState(true, false, EnumDialogType.Dialog); now += 1000; polling.OnTick(0);
        Require(dialog.Refreshes == 4 && !dialog.Focused, "Layer stole focus or dispatched to an unfocused dialog.");
        dialog.SetState(true, true, EnumDialogType.Dialog); polling.OnTick(0);
        Require(dialog.Refreshes == 5, "Regaining focus did not refresh stationary hover.");
        for (var i = 0; i < 10; i++) { now += 20; mapElement.CurrentBlockViewBounds.X1 += .1; polling.OnTick(.02f); }
        Require(dialog.Refreshes == 6, "Continuous panning refreshed Cairo at the native 50 Hz tick rate.");
        mouseX = -1; now += 1000; polling.OnTick(0); Require(dialog.Refreshes == 6, "Pointer outside map refreshed hover.");
        mouseX = 160; dialog.SetState(true, true, EnumDialogType.HUD); polling.OnTick(0);
        Require(dialog.Refreshes == 6, "Layer forced HUD minimap focus/hover.");
        dialog.SetState(false, true, EnumDialogType.Dialog); polling.OnTick(0);
        Require(dialog.Refreshes == 6, "Closed map received hover updates.");
        polling.Dispose(); composer.Dispose();
        Console.WriteLine("PASS native map manager/composer event dispatch: stationary cursor, pan, sample/age refresh, focus regain, closed/HUD/outside guards and no per-frame refresh");
        // Rain-height protection is distinct from the target water's own Y.
        var heights = (ushort[])Field(column, "Heights")!;
        heights[5 + 32 * 6] = 3;
        var pointMap = (IDictionary)Field(column, "Points")!;
        var pointList = (IList)pointMap[(1, 1)]!;
        var sourceType = type.GetNestedType("LocalSource", flags)!;
        pointList[0] = Activator.CreateInstance(sourceType, [5, 3, 6, 3, new PollutionLoad(1, 0, 0, 0, 0), PathogenLoads.Empty, true, 1d, 1d, OrganicWasteKind.Fecal, 1d, 0L])!;
        Require(((PollutionLoad)Call(store, "LoadAt", new BlockPos(6, 2, 6, 0))!).Fecal == 0,
            "Surface runoff crossed higher terrain into a well below that terrain.");
        var quality = mod.GetType("Seraph.World.WaterQuality", true)!;
        quality.GetMethod("BindPollution", flags)!.Invoke(null, [world, store]);
        // Exercise the native growth postfix with a real cached region. It must not perform a scan.
        var cropBridge = mod.GetType("Seraph.World.NativeCropPollution", true)!;
        var soil = new BlockEntityFarmland { Api = (ICoreAPI)api, Pos = new BlockPos(5, 3, 6, 0) };
        var cachedRegion = sample.GetType().GetProperty("Region")!;
        var originalRegion = cachedRegion.GetValue(sample);
        cachedRegion.SetValue(sample, new RegionalEnvironment { HeavyMetals = 1, SoilNitrogen = 1, SoilPhosphorus = 1 });
        var beforeGrowthReads = reads;
        object?[] growthArgs = [soil, 12d];
        cropBridge.GetMethod("Growth", flags)!.Invoke(null, growthArgs);
        Require((double)growthArgs[1]! == 12 && reads == beforeGrowthReads,
            "Native growth applied an environmental multiplier or triggered a world scan.");
        cachedRegion.SetValue(sample, originalRegion);
        var cropAccessor = (IBlockAccessor)Proxy(typeof(IBlockAccessor), (m, _) => m.Name == "GetBlockEntity" ? soil : throw new Exception(m.Name));
        var cropWorld = (IWorldAccessor)Proxy(typeof(IWorldAccessor), (m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_BlockAccessor" => cropAccessor, _ => throw new Exception(m.Name) });
        var produce = new ItemStack(new Item { Code = new("game:carrot"), NutritionProps = new() { FoodCategory = EnumFoodCategory.Vegetable } }, 3);
        var seeds = new ItemStack(new Item { Code = new("game:seeds-carrot"), NutritionProps = new() { FoodCategory = EnumFoodCategory.Vegetable } });
        const string pathogenKey = "vsslssmod:pathogens-v1";
        soil.CropAttributes.SetString(pathogenKey, "{\"ecoli\":0.2}");
        var cropLoads = (IReadOnlyDictionary<string, double>)cropBridge.GetMethod("ReadForInfo", flags)!.Invoke(null, [soil])!;
        var cropLevel = cropBridge.GetMethod("InfoLevel", flags)!;
        Require(cropLoads.TryGetValue("ecoli", out var cropLoad) && Math.Abs(cropLoad - .2) < .0001
            && (string)cropLevel.Invoke(null, [.0])! == "crop-pollution-clean"
            && (string)cropLevel.Invoke(null, [.2])! == "crop-pollution-moderate"
            && (string)cropLevel.Invoke(null, [.5])! == "crop-pollution-high",
            "Farmland pathogen info did not expose persisted load levels for native tooltips.");
        cropBridge.GetMethod("Harvest", flags)!.Invoke(null, [cropWorld, soil.Pos.UpCopy(), new[] { produce, seeds }]);
        Require(produce.Attributes.GetString(pathogenKey).Contains("ecoli") && !seeds.Attributes.HasAttribute(pathogenKey)
            && produce.Clone().Attributes.GetString(pathogenKey) == produce.Attributes.GetString(pathogenKey),
            "Harvest/split lost crop pathogens or exposed seeds as food.");
        cropBridge.GetMethod("Clear", flags)!.Invoke(null, [soil]);
        Require(!soil.CropAttributes.HasAttribute(pathogenKey), "Harvested crop contamination leaked into the next planting.");
        Console.WriteLine("PASS native crop bridge: native growth timing, identified edible harvest, clone retention and crop reset");
        var baseline = new WaterBatch { VolumeL = 1, TemperatureC = 20, Source = "groundwater" };
        WaterBatch QualityAt(int x) => (WaterBatch)quality.GetMethod("ApplyPollution", flags)!.Invoke(null,
            [world, new BlockPos(x, 2, 6, 0), baseline, new SeraphServerConfig(), true, false])!;
        var protectedWell = QualityAt(6);
        var exposedWell = QualityAt(5);
        Require(protectedWell.Source != "fecal" && exposedWell.Source == "fecal"
            && exposedWell.EntericHazardPerL > protectedWell.EntericHazardPerL,
            "Face-adjacent exposed waste was not included in water quality immediately.");
        pointList[0] = Activator.CreateInstance(sourceType, [5, 3, 6, 3, new PollutionLoad(0, 0, 0, 0, 1), PathogenLoads.Empty, true, 1d, 1d, OrganicWasteKind.Fecal, 1d, 0L])!;
        var inorganic = QualityAt(5);
        Require(inorganic.Source == protectedWell.Source && inorganic.ChemicalHazardPerL == 0
            && inorganic.Turbidity == protectedWell.Turbidity && inorganic.EntericHazardPerL == protectedWell.EntericHazardPerL,
            "Removed chemical water pollution created a dose or changed the biological water quality.");
        // A clean neighbour can skip ecology and have no AddSource calls at all.
        var cleanKey = Activator.CreateInstance(keyType, [1, 0, 0])!;
        var cleanColumn = Activator.CreateInstance(columnType, true)!;
        ((IDictionary)Field(cleanColumn, "Chunks")!)[0] = chunk;
        columnType.GetField("Snapshot", flags)!.SetValue(cleanColumn, sample);
        columnType.GetField("EcologyDirty", flags)!.SetValue(cleanColumn, false);
        columnType.GetField("EcologyHour", flags)!.SetValue(cleanColumn, hours + 1);
        ((IDictionary)Field(store, "columns")!)[cleanKey] = cleanColumn;
        var cleanScan = Activator.CreateInstance(scanType, flags, null, [cleanKey, cleanColumn, hours, new SeraphServerConfig()], null)!;
        Require(!(bool)Field(cleanScan, "SurveyEcology")! && Field(cleanScan, "Heights") is null,
            "Test did not exercise the local-only clean-column refresh.");
        Call(store, "Finish", cleanScan, now);
        Require(((ushort[])Field(cleanColumn, "Heights")!).Length == 1024,
            "A clean-column refresh lost the terrain barrier needed for neighbouring sources.");
        ((IDictionary)Field(store, "columns")!).Remove(cleanKey);
        // Removed sources retain the spatial reservoir, without a block read or an uphill leak.
        var residueType = type.GetNestedType("Residue", flags)!;
        var residues = (IDictionary)Field(column, "Residues")!;
        residues[(5, 3, 6)] = Activator.CreateInstance(residueType, [pointList[0]!, new RegionalEnvironment {
            SurfacePathogens = .3, GroundPathogens = .4,
            GroundAgents = new Dictionary<string, double> { ["giardia"] = .2 } }])!;
        pointMap.Clear(); Call(store, "IndexResidues", column);
        var residueWater = QualityAt(5);
        Require(QualityAt(6).PathogensPerL.Count == 0
            && QualityAt(6).EntericHazardPerL == protectedWell.EntericHazardPerL,
            "Persisted groundwater pollution crossed the rain-height barrier.");
        Require(residueWater.EntericHazardPerL > protectedWell.EntericHazardPerL
            && Math.Abs(residueWater.PathogensPerL.GetValueOrDefault("giardia") - .2 * .8) < 1e-9,
            "Removing a source erased its stored generic or identified groundwater burden.");
        var restoredColumn = Activator.CreateInstance(columnType, true)!;
        ((IDictionary)Field(restoredColumn, "Chunks")!)[0] = chunk;
        Call(store, "Save");
        Call(store, "Restore", restoredColumn);
        Require(((IDictionary)Field(restoredColumn, "Residues")!).Count == 1,
            "Saved local contamination reservoirs were lost on reload.");
        foreach (var suffix in new[] { "", "-sites", "-extracted" })
        {
            var persistedKey = "vsslssmod:environment-v1" + suffix;
            var original = persisted.GetValueOrDefault(persistedKey);
            var invalid = System.Text.Encoding.UTF8.GetBytes("null"); persisted[persistedKey] = invalid;
            var rejectedColumn = Activator.CreateInstance(columnType, true)!;
            ((IDictionary)Field(rejectedColumn, "Chunks")!)[0] = chunk;
            try { Call(store, "Restore", rejectedColumn); throw new Exception("Corrupt regional data accepted."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
            Require((bool)Field(rejectedColumn, "FormatRejected")!, "Rejected region did not block future persistence.");
            Require(persisted[persistedKey].SequenceEqual(invalid), "Rejected regional data overwritten.");
            if (original is null) persisted.Remove(persistedKey); else persisted[persistedKey] = original;
        }
        var clock = mod.GetType("Seraph.World.WorldCalendarTime", true)!;
        var clockRead = clock.GetMethod("Seconds", flags)!;
        var beforeClock = (double)clockRead.Invoke(null, [world])!;
        hours += 2;
        Require(Math.Abs((double)clockRead.Invoke(null, [world])! - beforeClock - 7200) < 1e-6,
            "Calendar clock lost loaded elapsed time.");
        hours += 100; clock.GetMethod("Reset", flags)!.Invoke(null, [world]);
        Require(Math.Abs((double)clockRead.Invoke(null, [world])! - beforeClock - 7200) < 1e-6,
            "Administrator time adjustment earned biological time.");
        Require(persisted.ContainsKey("vsslssmod:environment-v1"), "Regional state was never persisted.");
        Call(store, "Reconcile", now);
        loaded.Clear();
        Call(store, "Reconcile", now);
        Require(Call(store, "Get", pos) is null, "A detached chunk remained active until Disposed was set.");
        hooks["ChunkColumnLoaded"].DynamicInvoke(new Vec2i(0, 0), new IWorldChunk[] { (IWorldChunk)chunk });
        Require(((IDictionary)Field(store, "columns")!).Count == 1, "Chunk load events did not register a column.");
        hooks["ChunkColumnUnloaded"].DynamicInvoke(new Vec3i(0, 0, 0));
        Require(((IDictionary)Field(store, "columns")!).Count == 0, "Chunk unload events retained the column.");
        disposed = true;
        Require(Call(store, "Get", pos) is null && ((PollutionLoad)Call(store, "LoadAt", pos)!).Fecal == 0,
            "Unloaded chunks still contributed active pollution.");
        Require(reads == beforeLookup, "Unloaded inspection accessed chunk blocks.");
        store.Dispose();
        Require(hooks.Count == 0, "Pollution event handlers leaked after disposal.");
        Console.WriteLine("PASS native chunk pollution capture: bounded batches, empty containers, canopy, cached lookup, dimension, persistence, map roundtrip and load/unload events");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class RecordingMapDialog : GuiDialogWorldMap
    {
        private RecordingMapDialog() : base(null!, null!, null!, null!) { }
        internal int Refreshes;
        internal void SetState(bool open, bool focus, EnumDialogType type) { opened = open; focused = focus; dialogType = type; }
        public override void OnMouseMove(MouseEvent args) => Refreshes++;
    }
}



