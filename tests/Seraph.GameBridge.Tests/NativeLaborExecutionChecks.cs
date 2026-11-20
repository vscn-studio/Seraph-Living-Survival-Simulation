using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Seraph;
using Seraph.Application;
using Seraph.Client;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.GameContent;

// Run native bodies with the installed Harmony patches, including client-only mining.
internal static class NativeLaborExecutionChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static void Run(Assembly mod)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        long now = 100000;
        var slot = new DummySlot();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : throw new Exception(m.Name));
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, inventory);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        entity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        var pos = new BlockPos(0, 0, 0);
        player.CurrentBlockSelection = new() { Position = pos, HitPosition = new(.5, .5, .5) };
        var block = new Block { BlockId = 7, Code = new("game:stone-fixture"), BlockBehaviors = [] };
        var currentBlock = block;
        var chunkType = typeof(IBlockAccessor).GetMethod("GetChunkAtBlockPos", [typeof(BlockPos)])!.ReturnType;
        var chunk = RoutingProxy.Make(chunkType, (_, _) => null);
        var blocks = Proxy<IBlockAccessor>((m, _) => m.Name switch {
            "GetBlock" or "GetBlockRaw" => currentBlock, "GetChunkAtBlockPos" => chunk,
            "MarkBlockDirty" or "MarkBlockEntityDirty" => null, _ => throw new Exception("Labor blocks: " + m.Name)
        });
        var access = true;
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? access : throw new Exception(m.Name));
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? 10d : throw new Exception(m.Name));
        var logger = Proxy<ILogger>((_, _) => null);
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "PlayerByUid" => player, "get_ElapsedMilliseconds" => now,
            "get_Calendar" => calendar, "get_BlockAccessor" => blocks, "get_Claims" => claims,
            "get_Rand" => new Random(1), "PlaySoundAt" => null, _ => throw new Exception("Labor world: " + m.Name)
        });
        var system = new SeraphModSystem();
        var recipes = new RecipeRegistrySystem { KnappingRecipes = [new KnappingRecipe { Voxels = new bool[16, 16, 16] }] };
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem"
            ? m.GetGenericArguments()[0] == typeof(RecipeRegistrySystem) ? recipes : system : throw new Exception(m.Name));
        var save = RoutingProxy.Make(typeof(IWorldManagerAPI).GetProperty("SaveGame")!.PropertyType,
            (m, _) => m.Name == "get_SavegameIdentifier" ? "labor-world" : throw new Exception(m.Name));
        var manager = Proxy<IWorldManagerAPI>((m, _) => m.Name == "get_SaveGame" ? save : throw new Exception(m.Name));
        var cache = new Dictionary<string, object> { ["totalMsBlockBreaking"] = now };
        var registry = Proxy<IClassRegistryAPI>((m, _) => m.Name == "CreateInvNetworkUtil" ? null : throw new Exception(m.Name));
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_World" => world, "get_WorldManager" => manager,
            "get_ModLoader" => loader, "get_Logger" => logger, "get_ObjectCache" => cache,
            "get_ClassRegistry" => registry,
            _ => throw new Exception("Labor API: " + m.Name)
        });
        entity.World = world; entity.Api = api;
        typeof(EntityPlayer).GetProperty("Properties")!.SetValue(entity,
            new EntityProperties { Server = new EntityServerProperties([], new()) });
        var type = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(type, [api])!;
        var capabilities = CapabilityRegistry.Create(new(), []);
        type.GetField("capabilities", All)!.SetValue(server, capabilities);
        type.GetField("barrier", All)!.SetValue(server, new SideEffectBarrier("survival", capabilities));
        typeof(SeraphModSystem).GetField("server", All)!.SetValue(system, server);
        var st = type.GetNestedType("Session", All)!;
        var session = Activator.CreateInstance(st, All, null, [player], null)!;
        ((IDictionary)type.GetField("sessions", All)!.GetValue(server)!).Add(player.PlayerUID, session);
        void Set(string name, object value) => st.GetProperty(name)!.SetValue(session, value);
        T Get<T>(string name) => (T)st.GetProperty(name)!.GetValue(session)!;
        object? Call(string name, params object?[] args) => type.GetMethod(name, All)!.Invoke(server, args);
        Set("Environment", Activator.CreateInstance(st.GetProperty("Environment")!.PropertyType)!);
        Set("LastGoodSampleSeconds", now / 1000d);
        Set("HelloAccepted", true);
        FrameworkState Frame() => Get<FrameworkState>("Framework");
        double StaminaCapacity() => Exertion.StaminaCapacity(BodyFunctionResolver.Evaluate(Frame(), Get<BodyState>("State"), Get<BodyProfile>("Profile")).LaborEndurance, Frame().Nutrition);
        void Near(double actual, double expected, string error)
        { if (Math.Abs(actual - expected) > 1e-6) throw new Exception($"{error}: {actual} != {expected}"); }
        using var actions = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeInjuryActionBridge", true)!, true)!;
        using var labor = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeLaborBridge", true)!, true)!;

        // Exercise the actual patched native frame advance with authoritative server functions.
        var attackManager = new AnimationManager();
        var sharedAttack = new AnimationMetaData { Code = "swing", Animation = "swing", AnimationSpeed = 1 };
        attackManager.ActiveAnimationsByAnimCode["swing"] = sharedAttack;
        Seraph.Server.AdrenalineAttackTiming.Tag(attackManager, entity, "swing");
        var tagged = attackManager.ActiveAnimationsByAnimCode["swing"];
        if (ReferenceEquals(tagged, sharedAttack)) throw new Exception("Melee metadata still shares asset state.");
        Seraph.Server.AdrenalineAttackTiming.Tag(attackManager, entity, "swing");
        if (!ReferenceEquals(tagged, attackManager.ActiveAnimationsByAnimCode["swing"])) throw new Exception("Melee metadata cloned each cycle.");
        var animation = new Animation { QuantityFrames = 100 };
        var normalSwing = new RunningAnimation { meta = tagged, Animation = animation, Active = true };
        normalSwing.Progress(.1f, 1);
        var originalFrame = Frame();
        Set("Framework", originalFrame with { AdrenalineLevel = 1, AdrenalineRemainingSeconds = 20 });
        var fastSwing = new RunningAnimation { meta = tagged, Animation = animation, Active = true };
        fastSwing.Progress(.1f, 1);
        Near(fastSwing.CurrentFrame, normalSwing.CurrentFrame * 1.2, "Adrenaline did not advance real melee frames 20% faster");
        var unrelated = new RunningAnimation { meta = sharedAttack, Animation = animation, Active = true };
        unrelated.Progress(.1f, 1);
        Near(unrelated.CurrentFrame, normalSwing.CurrentFrame, "Adrenaline changed shared or unrelated animation");
        Set("Framework", originalFrame);
        var expiredSwing = new RunningAnimation { meta = tagged, Animation = animation, Active = true };
        expiredSwing.Progress(.1f, 1);
        Near(expiredSwing.CurrentFrame, normalSwing.CurrentFrame, "Expired adrenaline retained attack speed");
        Console.WriteLine("PASS actual native melee timing, expiry and shared-animation isolation");

        var clientEntity = new EntityPlayer { Alive = true };
        var clientPlayer = FixturePlayer.Make(clientEntity, inventory);
        clientPlayer.WorldData = player.WorldData; clientPlayer.CurrentBlockSelection = player.CurrentBlockSelection;
        clientEntity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        var local = FixtureClientPlayer.Make(clientEntity);
        var clientWorld = Proxy<IClientWorldAccessor>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Client, "get_Player" => local, "PlayerByUid" => clientPlayer,
            "get_ElapsedMilliseconds" => now, "get_BlockAccessor" => blocks,
            _ => throw new Exception("Labor client world: " + m.Name)
        });
        var warnings = new List<string>();
        var mouse = new MouseButtonState();
        var gui = Proxy<IGuiAPI>((m, _) => m.Name == "get_OpenedGuis" ? new List<GuiDialog>() : throw new Exception(m.Name));
        var input = Proxy<IInputAPI>((m, _) => m.Name == "get_MouseButton" ? mouse : throw new Exception(m.Name));
        object? Warn(object?[]? args) { warnings.Add((string)args![2]!); return null; }
        var clientApi = Proxy<ICoreClientAPI>((m, args) => m.Name switch {
            "get_World" => clientWorld, "get_ModLoader" => loader, "get_ObjectCache" => cache, "get_ClassRegistry" => registry,
            "get_IsGamePaused" => false, "get_Input" => input, "get_Gui" => gui,
            "TriggerIngameError" => Warn(args),
            _ => throw new Exception("Labor client API: " + m.Name)
        });
        clientEntity.World = clientWorld; clientEntity.Api = clientApi;
        var sent = new List<NativeWorkReceipt>();
        var intents = new List<WorkIntent>();
        Exception? receiptFailure = null;
        var channel = Proxy<IClientNetworkChannel>((m, args) => m.Name switch {
            "get_Connected" => true,
            "SendPacket" => args![0] is WorkIntent intent ? CaptureIntent(intent) : Capture((NativeWorkReceipt)args[0]!),
            _ => throw new Exception(m.Name)
        });
        object? CaptureIntent(WorkIntent intent)
        { intents.Add(intent); Call("OnWorkIntent", player, intent); return null; }
        object? Capture(NativeWorkReceipt receipt)
        {
            sent.Add(receipt);
            try { Call("OnNativeWork", player, receipt); } catch (Exception ex) { receiptFailure = ex; }
            return null;
        }
        var client = (SeraphClientBridge)RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
        typeof(SeraphClientBridge).GetField("api", All)!.SetValue(client, clientApi);
        typeof(SeraphClientBridge).GetField("channel", All)!.SetValue(client, channel);
        typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(client, new ServerSnapshot {
            SessionEpoch = Get<string>("Epoch"), Functions = new() { Movement = true, Manipulation = true } });
        typeof(SeraphClientBridge).GetProperty("LastReceivedUtc")!.SetValue(client, DateTime.UtcNow);
        typeof(SeraphModSystem).GetProperty("ClientBridge")!.SetValue(system, client);
        typeof(CollectibleObject).GetField("api", All)!.SetValue(block, clientApi);

        var before = Frame().Stamina;
        block.OnGettingBroken(clientPlayer, player.CurrentBlockSelection, slot, 10, .1f, 1);
        if (receiptFailure is not null) throw new Exception("Native receipt failed", receiptFailure);
        if (sent.Count != 1) throw new Exception("Actual client mining did not emit a native receipt.");
        Near(before - Frame().Stamina, .018 * .1 / StaminaCapacity(), "Client mining did not reach server stamina");
        var paid = Frame().Stamina;
        Call("OnNativeWork", player, sent[0]);
        Near(Frame().Stamina, paid, "Mining replay charged twice");
        var receipt = new NativeWorkReceipt { SessionEpoch = Get<string>("Epoch"), Sequence = 100, Seconds = .1, BlockId = 7 };
        access = false; Call("OnNativeWork", player, receipt); access = true;
        receipt.SessionEpoch = "old-session"; Call("OnNativeWork", player, receipt);
        receipt.SessionEpoch = Get<string>("Epoch"); receipt.X = 100; Call("OnNativeWork", player, receipt);
        receipt.X = 0; receipt.ToolId = 999; Call("OnNativeWork", player, receipt);
        Near(Frame().Stamina, paid, "Invalid native receipt charged work");
        var watts = Get<ActionEnergyBudget>("ActionEnergy").Consume(99.9, 100);
        Near(watts * 3600 / 4184, 450, "Hand work did not retain the expected kcal/game-hour tier");
        Near(Get<ActionEnergyBudget>("ActionEnergy").Consume(100, 101), 0, "Cancelled mining kept charging");
        Console.WriteLine("PASS actual native client mining -> receipt -> server reserves/450 kcal tier; replay, epoch, tool, reach, claims and cancel");

        // A fast native break may remove the target before its first work packet arrives.
        now += 200;
        Call("RecordNativeBlockBreak", player, 7, player.CurrentBlockSelection);
        currentBlock = new Block { BlockId = 0, Code = new("game:air") };
        var finalReceipt = new NativeWorkReceipt { SessionEpoch = Get<string>("Epoch"), Sequence = 2, Seconds = .1, BlockId = 7 };
        before = Frame().Stamina;
        Call("OnNativeWork", player, finalReceipt);
        Near(before - Frame().Stamina, .018 * .1 / StaminaCapacity(), "Native final break confirmation did not accept work");
        paid = Frame().Stamina;
        Call("OnNativeWork", player, finalReceipt);
        Near(Frame().Stamina, paid, "Native final break replay charged twice");
        now += 1500;
        finalReceipt.Sequence = 3;
        Call("OnNativeWork", player, finalReceipt);
        Near(Frame().Stamina, paid, "Expired native break confirmation charged work");
        currentBlock = block;
        Console.WriteLine("PASS final native block-break confirmation, replay and expiry");

        Set("RecoveryClock", new RecoveryClock()); Set("WorkIntervals", new WorkIntervals());
        Set("Framework", Frame() with { Stamina = .4 });
        Call("ChargeWork", entity, ExertionKind.Crafting, .1d, false);
        paid = Frame().Stamina;
        now += 1000; Call("RecoverExertion", session, 1d);
        var body = Get<BodyState>("State"); var profile = Get<BodyProfile>("Profile");
        var support = PlayerCondition.Evaluate(Frame(), body, profile).StaminaRecoveryMultiplier
            * BodyFunctionResolver.Evaluate(Frame(), body, profile).LaborEndurance;
        Near(Frame().Stamina - paid, 0, "Stamina recovered before continuous two-second rest");
        paid = Frame().Stamina;
        now += 1500; Call("RecoverExertion", session, 1.5d);
        Near(Frame().Stamina - paid, .5 / 50 * 1.2 * support, "Adapter did not recover only post-delay rest time");
        Call("ChargeWork", entity, ExertionKind.Crafting, .1d, false);
        paid = Frame().Stamina;
        now += 1000; Call("RecoverExertion", session, 1d);
        Near(Frame().Stamina - paid, 0, "Resumed labor did not restart rest delay");
        Console.WriteLine("PASS actual recovery adapter: continuous two-second delay, standing recovery and resumed work");
        Set("LastGoodSampleSeconds", now / 1000d);

        // An actual native grid transaction (including repeated shift-craft cycles), not a UI event.
        var tool = new CraftedTool { Code = new("game:axe-fixture"), ItemId = 20, Tool = EnumTool.Axe };
        var grid = new InventoryCraftingGrid("craftinggrid", player.PlayerUID, api);
        var output = grid[9]; output.Itemstack = new(tool);
        grid.MatchingRecipe = new GridRecipe { Output = new CraftingRecipeIngredient { ResolvedItemStack = output.Itemstack } };
        var consume = typeof(InventoryCraftingGrid).GetMethod("ConsumeIngredients", All)!;
        before = Frame().Stamina;
        for (var i = 0; i < 3; i++) consume.Invoke(grid, [output]);
        Near(before - Frame().Stamina, .0035 * .15 * .8 * 3 / StaminaCapacity(), "Native crafting batch was not charged as a brief impulse per transaction");
        // Real G posture: keep full native transaction time, discount only the work rates.
        now += 2000;
        Set("LastGoodSampleSeconds", now / 1000d);
        entity.ServerControls.FloorSitting = true;
        Set("ActionEnergy", new ActionEnergyBudget());
        before = Frame().Stamina;
        var lactateBefore = Frame().Blood.LactateG;
        consume.Invoke(grid, [output]);
        Near(before - Frame().Stamina, .0035 * .15 * .8 * .8 / StaminaCapacity(), "G seated crafting did not receive the work discount");
        Near(Frame().Blood.LactateG - lactateBefore, BloodCirculation.LactateRate(ExertionKind.Crafting) * .15 * .8,
            "G seated crafting lactate differs from predicted work");
        Near(Get<ActionEnergyBudget>("ActionEnergy").Consume(now / 1000d, now / 1000d + 1) * 3600 / 4184,
            160 * .8 * .15, "G seated crafting impulse energy did not receive exactly one discount");
        Near(Get<WorkIntervals>("WorkIntervals").RestSeconds(now / 1000d, now / 1000d + 1), 0,
            "Seated discount shortened the actual work interval");
        entity.ServerControls.FloorSitting = false;
        entity.ServerControls.Sneak = true;
        now += 2000; Set("LastGoodSampleSeconds", now / 1000d);
        before = Frame().Stamina; consume.Invoke(grid, [output]);
        Near(before - Frame().Stamina, .0035 * .15 * .8 / StaminaCapacity(), "Shift alone received the G seated work discount");
        entity.ServerControls.Sneak = false;
        Console.WriteLine("PASS native G seated crafting: 0.8 stamina/lactate/energy, full work duration and standing/Shift rates");
        grid.MatchingRecipe = null;
        paid = Frame().Stamina; consume.Invoke(grid, [output]);
        Near(Frame().Stamina, paid, "Empty crafting transaction charged labor");

        // Actual native knapping: changed voxel charges once, repeated/empty hit is free.
        slot.Itemstack = new(tool);
        var knap = new BlockEntityKnappingSurface { Pos = pos };
        typeof(BlockEntity).GetField("Api")!.SetValue(knap, api);
        typeof(BlockEntityKnappingSurface).GetField("selectedRecipeId", All)!.SetValue(knap, 0);
        knap.Voxels[4, 4] = true; knap.Voxels[12, 12] = true;
        var use = typeof(BlockEntityKnappingSurface).GetMethod("OnUseOver", All, null,
            [typeof(IPlayer), typeof(Vec3i), typeof(BlockFacing), typeof(bool)], null)!;
        before = Frame().Stamina;
        use.Invoke(knap, [player, new Vec3i(4, 0, 4), BlockFacing.UP, true]);
        if (knap.Voxels[4, 4]) throw new Exception("Native knapping did not mutate the voxel.");
        Near(before - Frame().Stamina, .0035 * .15 * .8 / StaminaCapacity(), "Accepted native knapping impulse charged incorrectly");
        paid = Frame().Stamina;
        use.Invoke(knap, [player, new Vec3i(4, 0, 4), BlockFacing.UP, true]);
        Near(Frame().Stamina, paid, "No-op knapping charged labor");
        // A changed chisel transaction settles once as light crafting, regardless of voxel count.
        now += 1000; Set("LastGoodSampleSeconds", now / 1000d);
        var laborType = mod.GetType("Seraph.Server.NativeLaborBridge", true)!;
        var startChisel = laborType.GetMethod("ChiselStart", All)!;
        var finishChisel = laborType.GetMethod("ChiselFinish", All)!;
        var markChisel = laborType.GetMethod("Chiseled", All)!;
        object?[] chiselArgs = [player, null];
        if (!(bool)startChisel.Invoke(null, chiselArgs)!) throw new Exception("Chisel transaction was rejected.");
        before = Frame().Stamina;
        markChisel.Invoke(null, [player, true]); markChisel.Invoke(null, [player, true]);
        finishChisel.Invoke(null, [null, chiselArgs[1]]);
        Near(before - Frame().Stamina, .0035 * .15 * .8 / StaminaCapacity(), "Chisel batch did not settle as one light impulse");
        now += 1000; Set("LastGoodSampleSeconds", now / 1000d);
        chiselArgs = [player, null]; startChisel.Invoke(null, chiselArgs);
        before = Frame().Stamina; finishChisel.Invoke(null, [null, chiselArgs[1]]);
        Near(Frame().Stamina, before, "Unchanged chisel operation charged stamina");
        Set("Framework", Frame() with { ExertionExhausted = true, Stamina = 0 });
        use.Invoke(knap, [player, new Vec3i(12, 0, 12), BlockFacing.UP, true]);
        if (!knap.Voxels[12, 12]) throw new Exception("Exhaustion allowed native voxel work.");
        if (Get<string>("DeniedReason") != "stamina") throw new Exception("Native denial omitted exhaustion reason.");
        output.Itemstack = new(tool);
        var sink = new DummySlot();
        var op = new ItemStackMoveOperation(world, EnumMouseButton.Left, (EnumModifierKey)0, EnumMergePriority.AutoMerge, 1)
            { ActingPlayer = player, MovedQuantity = 1 };
        var transferArgs = new object?[] { sink, op };
        var moved = (int)output.GetType().GetMethod("TryPutIntoNoEvent", All)!.Invoke(output, transferArgs)!;
        if (moved != 0 || ((ItemStackMoveOperation)transferArgs[1]!).MovedQuantity != 0 || !sink.Empty || output.Empty)
            throw new Exception("Exhaustion allowed a native batch/FlipWith crafting transfer.");
        Console.WriteLine("PASS actual native grid transaction batches, empty grid, accepted/no-op knapping and exhaustion stopping voxel mutation");
        Console.WriteLine("PASS native crafting transfer rechecks exhaustion inside batch and clears moved quantity");
        var warn = typeof(SeraphClientBridge).GetMethod("WarnExhaustion", All)!;
        client.Snapshot!.Functions!.ExhaustionReason = "stamina";
        warn.Invoke(client, [null]); warn.Invoke(client, [null]);
        if (warnings.Count != 1 || warnings[0].Contains("vsslssmod:") || warnings[0].Contains("{0}"))
            throw new Exception("Native red warning missing readable reason or cooldown.");
        now += 2500;
        client.Snapshot.Functions!.ExhaustionReason = "lactate";
        warn.Invoke(client, [null]);
        if (warnings.Count != 2 || warnings[0] == warnings[1]) throw new Exception("Native red warning omitted distinct lactate reason.");
        Console.WriteLine("PASS native red exhaustion error API with distinct reasons and cooldown");
        // Client executes a real crafting transaction against a nearly empty reserve.
        // The next native transfer must stop before either slot changes, without a server round trip.
        var receiveExertion = typeof(SeraphClientBridge).GetMethod("ReceiveExertion", All)!;
        var predictedFunctions = typeof(SeraphClientBridge).GetMethod("PredictedFunctions", All)!;
        var freshFunctions = new FunctionSnapshot { Movement = true, Manipulation = true };
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 1, Stamina = .002, Capacity = 100, Functions = freshFunctions }]);
        var localGrid = new InventoryCraftingGrid("craftinggrid", player.PlayerUID, clientApi);
        var localOutput = localGrid[9]; localOutput.Itemstack = new(tool);
        localGrid.MatchingRecipe = new GridRecipe { Output = new CraftingRecipeIngredient { ResolvedItemStack = localOutput.Itemstack } };
        consume.Invoke(localGrid, [localOutput]);
        if (((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanUseActiveHand)
            throw new Exception("Accepted local crafting did not exhaust predicted reserve immediately.");
        op = new ItemStackMoveOperation(clientWorld, EnumMouseButton.Left, (EnumModifierKey)0, EnumMergePriority.AutoMerge, 1)
            { ActingPlayer = clientPlayer, MovedQuantity = 1 };
        transferArgs = [sink, op];
        moved = (int)localOutput.GetType().GetMethod("TryPutIntoNoEvent", All)!.Invoke(localOutput, transferArgs)!;
        if (moved != 0 || !sink.Empty || localOutput.Empty)
            throw new Exception("Predicted exhaustion allowed local slot mutation before server correction.");
        // Empty burst reserves retain sustained running and preserve native walking speed.
        typeof(EntityPlayer).GetField("walkSpeed", All)!.SetValue(clientEntity, 1f);
        clientEntity.ServerControls.Sprint = true;
        var exhaustedSpeed = clientEntity.GetWalkSpeedMultiplier();
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 2, Acknowledged = 1, Stamina = .3, Capacity = 100, Functions = freshFunctions }]);
        var recoveredSpeed = clientEntity.GetWalkSpeedMultiplier();
        Near((1 + (Vintagestory.API.Config.GlobalConstants.SprintSpeedMultiplier - 1)
            * .8) / (1 + (Vintagestory.API.Config.GlobalConstants.SprintSpeedMultiplier - 1) * .25) * exhaustedSpeed, recoveredSpeed,
            "Native sprint gate changed base walking or retained sprint");
        if (exhaustedSpeed <= 0 || !clientEntity.ServerControls.Sprint)
            throw new Exception("Native sprint gate removed real intent or stopped walking.");
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 1, Stamina = 0, Capacity = 100, Exhausted = true }]);
        if (!((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanUseActiveHand)
            throw new Exception("Reordered state restored old exhaustion.");
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 3, Life = 1, Stamina = 1, Capacity = 100, Functions = freshFunctions }]);
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 4, Life = 0, Stamina = 0, Capacity = 100, Exhausted = true }]);
        if (!((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanUseActiveHand)
            throw new Exception("Prior-life state leaked into respawn.");
        Console.WriteLine("PASS actual client crafting pre-mutation stop, native sprint speed, acknowledgment, reordering and life reset");
        // Mining is executed by the native client. A productive frame reserves its cost
        // immediately, so the following frame cannot advance resistance while awaiting authority.
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 5, Life = 1, Stamina = .001, Capacity = 100, Functions = freshFunctions }]);
        Set("ExertionLife", 1); Set("NativeWork", new NativeWorkReceipts());
        Set("WorkAcknowledgements", new WorkAcknowledgements());
        Set("Framework", Frame() with { Stamina = .001, ExertionExhausted = false });
        slot.Itemstack = null;
        now += 200; Set("LastGoodSampleSeconds", now / 1000d);
        cache["totalMsBlockBreaking"] = now;
        var resistance = block.OnGettingBroken(clientPlayer, player.CurrentBlockSelection, slot, 10, .1f, 1);
        if (resistance >= 10 || ((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanUseActiveHand)
            throw new Exception("Native productive mining frame did not reserve its cost locally.");
        var receiptCount = sent.Count;
        Near(block.OnGettingBroken(clientPlayer, player.CurrentBlockSelection, slot, 10, .1f, 1), 10,
            "Predicted exhaustion allowed the next native mining frame to advance");
        if (sent.Count != receiptCount) throw new Exception("Blocked mining emitted another paid-work receipt.");
        Console.WriteLine("PASS actual local mining stops before resistance changes on the next exhausted frame");
        // UI mouse buttons are real intent even when native controls suppress an exhausted action.
        // Intent packets acknowledge accepted native work but never charge another fee.
        var poll = typeof(SeraphClientBridge).GetMethod("PollExertion", All)!;
        Set("Framework", Frame() with { Stamina = .4, ExertionExhausted = true });
        mouse.Left = true;
        paid = Frame().Stamina;
        poll.Invoke(client, null);
        Near(Frame().Stamina, paid, "Prediction intent charged another native fee");
        if (intents.Count != 1 || !intents[0].TriggerHeld) throw new Exception("Physical UI button intent was lost.");
        Call("TickExertion", session, true, .05d);
        if (!Get<bool>("SprintExhausted")) throw new Exception("Held UI button incorrectly unlocked exhaustion.");
        mouse.Left = false; now += 250;
        poll.Invoke(client, null);
        Call("TickExertion", session, true, .05d);
        if (!Get<bool>("SprintExhausted")) throw new Exception("Release bypassed the continuous rest delay.");
        now += 2100;
        Call("TickExertion", session, true, 2.1d);
        Call("TickExertion", session, true, .05d);
        if (Get<bool>("SprintExhausted")) throw new Exception("Released UI button did not unlock recovered exhaustion.");
        Set("ExertionSequence", 5L);
        receiveExertion.Invoke(client, [Call("ExertionProjection", session)]);
        if (!((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanUseActiveHand)
            throw new Exception("Lightweight unlock retained denied functions from an older full snapshot.");
        Console.WriteLine("PASS physical UI intent, zero extra fees, release unlock and immediate authoritative function refresh");
        var recoveryRate = actions.GetType().GetMethod("RecoveryRate", All)!;
        var clientBreath = new EntityBehaviorBreathe(clientEntity);
        var oxygenFunctions = (FunctionSnapshot)predictedFunctions.Invoke(client, null)!;
        oxygenFunctions.OxygenReplacement = true;
        Near((float)recoveryRate.Invoke(null, [10000f, clientBreath])!, 0, "Client retained native refill during oxygen replacement");
        oxygenFunctions.OxygenReplacement = false;
        Near((float)recoveryRate.Invoke(null, [10000f, clientBreath])!, 10000, "Client suppressed unowned oxygen recovery");
        Console.WriteLine("PASS client oxygen ownership enables and releases native recovery suppression");

        // Repeated exhausted native physics queries must not clone immutable state or
        // rebuild complete body-function projections on every walk-speed calculation.
        Set("LastGoodSampleSeconds", now / 1000d);
        Set("Framework", Frame() with { Stamina = 0, ExertionExhausted = true });
        var functionFrame = Frame();
        var functionArgs = new object?[] { entity };
        var actionFunctions = type.GetMethod("ActionFunctions", All)!;
        var denied = (FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!;
        if (!denied.CanSprint || denied.CanUseActiveHand || denied.PaceGain != .25) throw new Exception("Exhausted cache allowed work.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
            if (!ReferenceEquals(denied, actionFunctions.Invoke(server, functionArgs)))
                throw new Exception("Unchanged native function query rebuilt its snapshot.");
        var allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (!ReferenceEquals(functionFrame, Frame())) throw new Exception("Native query cloned unchanged framework state.");
        var report = $"10,000 exhausted server queries: {timer.Elapsed.TotalMilliseconds:0.00} ms, {allocation} bytes (reflection/proxy fixture; not game FPS).";
        Console.WriteLine("PASS exhausted native projection reuse; " + report);
        File.WriteAllText(TestArtifacts.FilePath("exertion-performance.txt"), report);
        Set("Framework", Frame() with { Stamina = .8, ExertionExhausted = false });
        var recovered = (FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!;
        if (!recovered.CanSprint || ReferenceEquals(denied, recovered)) throw new Exception("Recovery retained cached exhaustion.");
        entity.WatchedAttributes.SetFloat("intoxication", 1);
        var intoxicated = actionFunctions.Invoke(server, functionArgs);
        if (ReferenceEquals(recovered, intoxicated)) throw new Exception("Native intoxication did not invalidate functions.");
        var previousBody = Get<BodyState>("State");
        Set("State", previousBody with { SleepPressure = .9 });
        if (ReferenceEquals(intoxicated, actionFunctions.Invoke(server, functionArgs))) throw new Exception("Body mutation retained stale functions.");
        Set("State", previousBody);
        now += 6000;
        if (actionFunctions.Invoke(server, functionArgs) is not null) throw new Exception("Cached functions bypassed stale-environment guard.");
        Set("LastGoodSampleSeconds", now / 1000d);
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 100, Life = 1, Stamina = 0, Capacity = 100, Functions = freshFunctions }]);
        var localDenied = predictedFunctions.Invoke(client, null);
        for (var i = 0; i < 10000; i++)
            if (!ReferenceEquals(localDenied, predictedFunctions.Invoke(client, null))) throw new Exception("Exhausted client prediction rebuilt each physics query.");
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 101, Life = 1, Stamina = 1, Capacity = 100, Functions = freshFunctions }]);
        if (!((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanSprint) throw new Exception("Client cache did not release after reconciliation.");
        Console.WriteLine("PASS exhausted server/client cache reuse and immediate state, native alcohol, body, freshness and recovery invalidation");

        entity.WatchedAttributes.SetFloat("intoxication", 0);
        entity.ServerControls.Sprint = false; entity.Controls.Sprint = false;
        entity.ServerControls.Jump = false; entity.ServerControls.Forward = true;
        Set("Framework", FrameworkState.Create("escape") with { Stamina = 0, ExertionExhausted = true });
        Set("WorkIntervals", new WorkIntervals()); Set("RecoveryClock", new RecoveryClock());
        Set("JumpCooldownRemaining", 3d);
        Get<RecoveryClock>("RecoveryClock").RecordWork(now / 1000d);
        Call("TickExertion", session, false, 10d);
        Near(Get<double>("JumpCooldownRemaining"), 3, "Inactive tick advanced cooldown");
        now += 2000; Set("LastGoodSampleSeconds", now / 1000d);
        Call("TickExertion", session, true, 2d);
        Near(Frame().Stamina, 0, "Exhausted walking skipped rest delay");
        now += 1000; Set("LastGoodSampleSeconds", now / 1000d);
        Call("TickExertion", session, true, 1d);
        Near(Frame().Stamina, .012, "Exhausted walking did not recover 1.2 percentage points per second");
        if (!((FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!).CanJump)
            throw new Exception("Cooldown expiry retained cached jump denial");

        entity.ServerControls.Forward = false;
        Set("Framework", Frame() with { Stamina = 0 });
        Set("ActionEnergy", new ActionEnergyBudget());
        var ground = new PModuleOnGround();
        entity.ServerControls.Jump = true;
        entity.Pos.Motion.Y = 0;
        var jumpLactate = Frame().Blood.LactateG;
        ground.DoApply(.05f, entity, entity.Pos, entity.ServerControls);
        Near(entity.Pos.Motion.Y, Vintagestory.API.Config.GlobalConstants.BaseJumpForce / 60d,
            "Exhausted native takeoff lost one-block clearance");
        Near(Frame().Stamina, 0, "Escape jump produced negative stamina");
        Near(Frame().Blood.LactateG - jumpLactate, 0, "Actual escape jump incorrectly charged lactate");
        Near(Get<double>("JumpCooldownRemaining"), 3, "Actual takeoff did not start cooldown");
        Near(Get<ActionEnergyBudget>("ActionEnergy").Consume(now / 1000d, now / 1000d + 1) * 3600 / 4184,
            450, "Actual escape jump energy was not charged");
        Near(Get<WorkIntervals>("WorkIntervals").RestSeconds(now / 1000d, now / 1000d + 1), 0,
            "Escape jump omitted work interval");
        entity.Pos.Motion.Y = 0; now += 600;
        ground.DoApply(.05f, entity, entity.Pos, entity.ServerControls);
        Near(entity.Pos.Motion.Y, 0, "Native jump bypassed exhausted cooldown");
        Near(Frame().Blood.LactateG - jumpLactate, 0, "Rejected jump charged another fee");
        Near(Get<double>("JumpCooldownRemaining"), 3, "Rejected jump extended cooldown");
        entity.ServerControls.Jump = false;
        Call("TickExertion", session, true, 3d);
        Near(Get<double>("JumpCooldownRemaining"), 0, "Active time did not expire cooldown");
        Set("LastGoodSampleSeconds", now / 1000d);
        entity.ServerControls.Jump = true;
        var beforeSecondJump = Frame().Blood.LactateG;
        ground.DoApply(.05f, entity, entity.Pos, entity.ServerControls);
        if (entity.Pos.Motion.Y <= 0) throw new Exception("Native escape jump did not resume after cooldown");
        Near(Frame().Blood.LactateG - beforeSecondJump, 0, "Second escape jump incorrectly charged lactate");
        entity.ServerControls.Jump = false;
        var healthyJumpFrame = Frame();
        Set("Framework", Frame() with { Vitals = Frame().Vitals.Change(-15, 4) });
        Set("JumpCooldownRemaining", 0d);
        if (((FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!).CanJump)
            throw new Exception("Escape exception bypassed severe leg injury");
        Set("Framework", healthyJumpFrame with { Stamina = .20, Blood = new() { LactateG = 59 }, ExertionRecoveryStage = RecoveryStage.Recovering });
        entity.ServerControls.Forward = true;
        Call("TickExertion", session, true, .05d);
        if (Get<bool>("SprintExhausted")) throw new Exception("Walking could not unlock at 20% stamina and below 60% lactate");
        Console.WriteLine("PASS native exhausted escape: walking recovery, exact takeoff/blocked fees, cooldown, one-block force, injury and moving unlock");

        receiveExertion.Invoke(client, [new ExertionState { Sequence = 102, Life = 1, Stamina = 0, Exhausted = true,
            Capacity = 100, Functions = new() { BodyCanJump = true, CanJump = false }, JumpCooldownRemainingSeconds = 3 }]);
        var prediction = (WorkPrediction)typeof(SeraphClientBridge).GetField("workPrediction", All)!.GetValue(client)!;
        if (((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanJump) throw new Exception("Client ignored cooldown");
        prediction.Advance(3);
        if (!((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanJump) throw new Exception("Client cached denial beyond expiry");
        prediction.Add(ExertionKind.Jump, 1);
        if (((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanJump) throw new Exception("Local jump did not reserve cooldown");
        receiveExertion.Invoke(client, [new ExertionState { Sequence = 103, Life = 1, Stamina = 0, Exhausted = true,
            Capacity = 100, Functions = new() { BodyCanJump = false, CanJump = false } }]);
        prediction.Advance(3);
        if (((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).CanJump) throw new Exception("Client cooldown expiry erased injury");
        Console.WriteLine("PASS client jump cooldown reconciliation and cache expiry preserve body restrictions");

        var breath = new EntityBehaviorBreathe(entity);
        breath.GetType().GetField("oxygenTree", All)!.SetValue(breath, new Vintagestory.API.Datastructures.TreeAttribute());
        breath.MaxOxygen = 40000; breath.Oxygen = 20000; breath.HasAir = true;
        entity.Properties.Server.Behaviors.Add(breath);
        Set("Framework", FrameworkState.Create("breathing"));
        entity.ServerControls.Forward = entity.ServerControls.Sprint = true;
        now += 100; Set("LastGoodSampleSeconds", now / 1000d);
        Call("TickExertion", session, true, 1d);
        Near(breath.Oxygen / breath.MaxOxygen, .5 + 1d / 45, "Sprint paused or drained breathing");
        var oxygenBefore = breath.Oxygen;
        for (var i = 0; i < 4; i++) Call("ChargeWork", entity, ExertionKind.Crafting, .1d, false);
        Near(breath.Oxygen, oxygenBefore, "Multiple work receipts changed airborne oxygen");
        Call("TickExertion", session, false, 10d);
        Near(breath.Oxygen, oxygenBefore, "Inactive tick refilled oxygen");
        breath.OnGameTick(.1f);
        Near(breath.Oxygen, oxygenBefore, "Native refill duplicated continuous breathing");
        breath.HasAir = false; breath.Oxygen = 20000;
        Call("RecoverOxygen", session, 1d);
        Near(breath.Oxygen, 20000, "Underwater breathing created oxygen");
        Call("ApplyExertion", session, ExertionKind.Sprinting, 1d, false, null);
        Near(breath.Oxygen, 19200, "Underwater exercise depletion missing");
        breath.OnGameTick(.1f);
        Near(breath.Oxygen, 19100, "Native underwater depletion changed");
        breath.HasAir = true;
        Call("RecoverOxygen", session, 1d);
        Near(breath.Oxygen / breath.MaxOxygen, 19100d / 40000 + 1d / 45, "Surfacing did not immediately resume breathing");
        Set("Framework", FrameworkState.Create("running") with { HasAir = true });
        Set("RunningPace", new RunningPace()); Set("WorkIntervals", new WorkIntervals());
        Set("RecoveryClock", new RecoveryClock()); Set("IntentHeld", false);
        Set("ExertionSequence", 200L);
        entity.ServerControls.Sprint = entity.ServerControls.Forward = true;
        for (var i = 0; i < 12; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        var burstProjection = (FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!;
        Near(burstProjection.PaceGain, 1, "Full burst ended before 3 s");
        var staminaAtThree = Frame().Stamina;
        for (var i = 0; i < 4; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        Near(((FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!).PaceGain, .8,
            "Server did not blend burst into sustained running");
        if (Get<ExertionKind>("MovementActivity") != ExertionKind.Running || Frame().Stamina >= staminaAtThree)
            throw new Exception("Burst transition did not pay accepted movement costs.");
        var runningStart = Frame().Stamina;
        var runningLactate = Frame().Blood.LactateG;
        for (var i = 0; i < 40; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        Near(Frame().Stamina - runningStart, -.09 / StaminaCapacity(), "Running received passive walking recovery or wrong fee");
        Near(Frame().Blood.LactateG, runningLactate, "Healthy running added or erased lactate");
        Set("Framework", Frame() with { Stamina = 0, ExertionExhausted = true });
        now += 250; Set("LastGoodSampleSeconds", now / 1000d);
        Call("TickExertion", session, true, .25d);
        var runFunctions = (FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!;
        if (!runFunctions.CanSprint || runFunctions.CanUseActiveHand
            || Get<ExertionKind>("MovementActivity") != ExertionKind.Jogging)
            throw new Exception("Empty reserve prevented running or permitted strong labor.");
        for (var i = 0; i < 4; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        runFunctions = (FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!;
        Near(runFunctions.PaceGain, .25, "Empty reserve lost jogging pace");
        var runPacket = (ExertionState)Call("ExertionProjection", session)!;
        receiveExertion.Invoke(client, [runPacket]);
        Near(((FunctionSnapshot)predictedFunctions.Invoke(client, null)!).PaceGain, runFunctions.PaceGain,
            "Client/server running gain disagreed after authoritative reconciliation");
        var jogLactate = Frame().Blood.LactateG;
        for (var i = 0; i < 2400; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        Near(Frame().Stamina, 0, "Jogging recovered stamina");
        Near(Frame().Blood.LactateG, jogLactate, "Jogging incorrectly cleared lactate");
        Near(Get<double>("ActivityPowerW") * 3600 / 4184, 300, "Jogging stopped paying activity energy");
        Set("Framework", Frame() with { Stamina = .8 });
        now += 250; Set("LastGoodSampleSeconds", now / 1000d);
        Call("TickExertion", session, true, .25d);
        Near(((FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!).PaceGain, .25, "Refilling a held exhausted runner restored fast running");
        Set("Framework", Frame() with { Stamina = 0 });
        entity.ServerControls.Sprint = false;
        for (var i = 0; i < 8; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        Near(Frame().Stamina, 0, "Walking skipped recovery delay");
        for (var i = 0; i < 4; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
        }
        Near(Frame().Stamina, .012, "Walking recovery rate changed");
        Set("Framework", FrameworkState.Create("bounded-native-run") with { HasAir = true });
        Set("RunningPace", new RunningPace()); Set("WorkIntervals", new WorkIntervals());
        Set("RecoveryClock", new RecoveryClock());
        entity.ServerControls.Sprint = true;
        var slowAt = 0d; var emptyAt = 0d;
        for (var i = 1; i <= 800; i++)
        {
            now += 250; Set("LastGoodSampleSeconds", now / 1000d);
            Call("TickExertion", session, true, .25d);
            if (slowAt == 0 && Frame().Stamina < .2) slowAt = i * .25;
            if (emptyAt == 0 && Frame().Stamina == 0) emptyAt = i * .25;
        }
        if (slowAt < 135 || slowAt > 138 || emptyAt < 169 || emptyAt > 173)
            throw new Exception($"Native running timing: slowdown {slowAt}, empty {emptyAt}");
        Near(((FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!).PaceGain, .25, "Native run did not reach jogging");
        Console.WriteLine($"PASS native run slows at {slowAt:0.00} s, empties at {emptyAt:0.00} s; ten-minute jogging, held refill and walking recovery");
        Set("Framework", Frame() with { Vitals = Frame().Vitals.Change(-15, 4) });
        if (((FunctionSnapshot)actionFunctions.Invoke(server, functionArgs)!).CanSprint)
            throw new Exception("Sustained running bypassed a destroyed leg.");
        entity.Properties.Server.Behaviors.Remove(breath);
        Console.WriteLine("PASS authoritative 3+1 s burst, sustained running costs, empty-reserve escape, injury and client reconciliation");
        Console.WriteLine("PASS continuous native breathing: sprint, repeated receipts, inactive tick, underwater costs and immediate surfacing");
    }

    private sealed class CraftedTool : Item
    {
        public override bool ConsumeCraftingIngredients(ItemSlot[] slots, ItemSlot outputSlot, IRecipeBase matchingRecipe) => true;
    }
}
