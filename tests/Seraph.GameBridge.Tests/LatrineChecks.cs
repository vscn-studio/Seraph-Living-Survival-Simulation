using System.Reflection;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class LatrineChecks
{
    private sealed class PosePlayer : EntityPlayer
    {
        public override IAnimationManager AnimManager { get; set; } = null!;
    }
    internal static void Run(string root, string gamePath)
    {
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var adapter = typeof(BlockLatrine).Assembly.GetType("Seraph.World.LatrineWasteStorage", true)!;
        var read = adapter.GetMethod("Read", hidden)!;
        var matches = adapter.GetMethod("Matches", hidden)!;
        var add = adapter.GetMethod("Add", hidden)!;
        var local = new Item { Code = new("vsslssmod:manure"), ItemId = 101, MaxStackSize = 64,
            Attributes = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/itemtypes/manure.json")))["attributes"] };
        var slurryProps = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/itemtypes/manureslurryportion.json")))["attributes"];
        var localSlurry = new Item { Code = new("vsslssmod:manureslurryportion"), ItemId = 105, MaxStackSize = 5000, Attributes = slurryProps };
        var natural = new Item { Code = new("naturalfertilizer:manureslurryportion"), ItemId = 102, MaxStackSize = 5000, Attributes = slurryProps };
        var foreign = new Item { Code = new("game:grain-flax"), ItemId = 103 };
        var block = new BlockBarrel { Code = new("game:barrel"), BlockId = 70 };
        var latrinePos = new BlockPos(5, 21, 8, 0);
        Block currentBlock = block;
        BlockEntityBarrel? barrel = new() { Block = block, Pos = latrinePos.AddCopy(0, -1, 0) };
        var failDirty = false;
        var dirtyCount = 0;
        object? Dirty()
        {
            dirtyCount++;
            if (failDirty) { failDirty = false; throw new InvalidOperationException("Injected dirty failure"); }
            return null;
        }
        var airGap = 0;
        var obstructed = false;
        var chunk = RoutingProxy.Make(typeof(IBlockAccessor).GetMethod("GetChunkAtBlockPos", [typeof(BlockPos)])!.ReturnType, (_, _) => null);
        void CheckPos(BlockPos pos) => Require(pos.X == latrinePos.X && pos.Z == latrinePos.Z && pos.Y < latrinePos.Y, "Waste left the vertical toilet column.");
        var accessor = Proxy<IBlockAccessor>((method, args) =>
        {
            CheckPos((BlockPos)args![0]!);
            return method.Name switch
            {
                "GetChunkAtBlockPos" => chunk,
                "GetBlock" => ((BlockPos)args![0]!).Y == latrinePos.Y - airGap - 1 ? currentBlock
                    : new Block { BlockId = obstructed ? 99 : 0 }, "GetBlockEntity" => barrel,
                "MarkBlockEntityDirty" => Dirty(), "MarkBlockDirty" => null,
                _ => throw new Exception("Unexpected barrel accessor call " + method.Name)
            };
        });
        var naturalLoaded = false;
        var world = Proxy<IWorldAccessor>((method, args) => method.Name switch
        {
            "get_BlockAccessor" => accessor, "get_ElapsedMilliseconds" => 100L,
            "GetItem" => ((AssetLocation)args![0]!).Domain == "naturalfertilizer" ? (naturalLoaded ? natural : null) : localSlurry,
            "get_Side" => EnumAppSide.Server,
            _ => throw new Exception("Unexpected barrel world call " + method.Name)
        });
        var recipes = new RecipeRegistrySystem();
        var loader = Proxy<IModLoader>((method, _) => method.Name == "GetModSystem" ? recipes
            : throw new Exception("Unexpected barrel loader call " + method.Name));
        var core = Proxy<ICoreAPI>((method, _) => method.Name switch
        {
            "get_World" => world, "get_Side" => EnumAppSide.Server, "get_ModLoader" => loader,
            _ => throw new Exception("Unexpected barrel API call " + method.Name)
        });
        void Init(BlockEntityBarrel target) { target.Api = core; target.Inventory.Api = core; }
        Init(barrel);
        foreach (var item in new[] { local, localSlurry, natural, foreign })
            typeof(CollectibleObject).GetField("api", hidden)!.SetValue(item, core);
        object? Read() => read.Invoke(null, [world, latrinePos]);
        void Add(object storage, int count) => add.Invoke(storage, [world, count, null, null]);
        void RejectAdd(object storage, int count)
        {
            try { Add(storage, count); throw new Exception("Invalid barrel mutation accepted."); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        }
        currentBlock = new Block { BlockId = 0 };
        Require(Read() is null, "Missing barrel accepted.");
        currentBlock = block;
        barrel.Sealed = true; Require(Read() is null, "Sealed barrel accepted."); barrel.Sealed = false;
        barrel.Inventory[1].Itemstack = new ItemStack(foreign);
        Require(Read() is null, "Barrel containing liquid accepted."); barrel.Inventory[1].Itemstack = null;
        barrel.Inventory[0].Itemstack = new ItemStack(foreign);
        Require(Read() is null, "Foreign solid content accepted."); barrel.Inventory[0].Itemstack = null;
        barrel.Inventory[0].Itemstack = new ItemStack(new Item { Code = new("naturalfertilizer:compost"), ItemId = 104 });
        Require(Read() is null, "Non-manure Natural Fertilizer content accepted."); barrel.Inventory[0].Itemstack = null;
        var storage = Read() ?? throw new Exception("Empty barrel rejected.");
        Add(storage, 3);
        Require(barrel.Inventory[0].Empty && barrel.Inventory[1].Itemstack?.Item == localSlurry && barrel.Inventory[1].StackSize == 300
            && dirtyCount > 0 && barrel.Inventory.DirtySlots.Contains(1), "Three 1 kg portions did not produce 3 L of liquid slurry.");
        RejectAdd(storage, 3);
        Require(barrel.Inventory[1].StackSize == 300, "Stale action changed barrel contents.");
        Add(Read()!, 2);
        Require(barrel.Inventory[1].StackSize == 500, "Repeated use lost slurry.");
        storage = Read()!; failDirty = true; RejectAdd(storage, 2);
        Require(barrel.Inventory[0].Empty && barrel.Inventory[1].StackSize == 500, "Failed update did not restore both slots.");
        storage = Read()!;
        var originalBarrel = barrel;
        barrel = new() { Block = block, Pos = originalBarrel.Pos }; Init(barrel);
        barrel.Inventory[1].Itemstack = new ItemStack(localSlurry, 500);
        Require(!(bool)matches.Invoke(storage, [world])!, "Replacement barrel accepted by a stale action.");
        RejectAdd(storage, 1);
        barrel = originalBarrel;
        barrel.Inventory[1].Itemstack = new ItemStack(localSlurry, 4900);
        RejectAdd(Read()!, 2); Add(Read()!, 1);
        Require(barrel.Inventory[1].StackSize == 5000, "50 L capacity enforcement failed.");
        RejectAdd(Read()!, 1);
        barrel.Inventory[1].Itemstack = new ItemStack(localSlurry, 4950);
        Require((int)adapter.GetProperty("AvailablePortions", hidden)!.GetValue(Read())! == 0,
            "Half a litre of room accepted a 1 L bowel portion.");
        barrel.Inventory[1].Itemstack = null;
        naturalLoaded = true; Add(Read()!, 4);
        Require(barrel.Inventory[1].Itemstack?.Item == natural && barrel.Inventory[1].StackSize == 400,
            "Natural Fertilizer slurry was not preferred when available.");
        barrel.Inventory[1].Itemstack = new ItemStack(localSlurry, 200); Add(Read()!, 1);
        Require(barrel.Inventory[1].Itemstack?.Item == localSlurry && barrel.Inventory[1].StackSize == 300,
            "Enabling Natural Fertilizer replaced existing local slurry.");
        // Legacy solid manure moves to the liquid slot, retaining mass and pathogens.
        barrel.Inventory[1].Itemstack = null;
        barrel.Inventory[0].Itemstack = new ItemStack(local, 2);
        barrel.Inventory[0].Itemstack!.Attributes.SetDouble("vsslssmod:manureMassG", 1000);
        barrel.Inventory[0].Itemstack!.Attributes.SetString("vsslssmod:pathogens-v1", "{\"shigella\":0.5}");
        storage = Read()!; failDirty = true; RejectAdd(storage, 1);
        Require(barrel.Inventory[0].StackSize == 2 && barrel.Inventory[1].Empty, "Failed migration lost old solids.");
        Add(Read()!, 1);
        var mixed = barrel.Inventory[1].Itemstack!;
        Require(barrel.Inventory[0].Empty && mixed.StackSize == 300
            && Math.Abs(mixed.Attributes.GetDouble("vsslssmod:manureMassG") * mixed.StackSize - 3000) < 1e-9,
            "Legacy solid to slurry conversion lost or duplicated mass.");
        var loads = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(mixed.Attributes.GetString("vsslssmod:pathogens-v1"))!;
        Require(Math.Abs(loads["shigella"] * 3000 - 1000) < 1e-9, "Legacy pathogen dose was not conserved.");
        var split = mixed.Clone(); split.StackSize = 100;
        Require(Math.Abs(split.Attributes.GetDouble("vsslssmod:manureMassG") * 100 - 1000) < 1e-9,
            "Splitting slurry lost its per-item mass.");
        var pollution = typeof(BlockLatrine).Assembly.GetType("Seraph.World.ManureGroundStorage", true)!.GetMethod("ContainsManure", hidden)!;
        Require((bool)pollution.Invoke(null, [barrel])!, "Slurry in a barrel no longer counts as fecal pollution.");
        var mixture = typeof(BlockLatrine).Assembly.GetType("Seraph.World.NativeWaterTransfer", true)!.GetMethod("SlurryMixture", hidden)!;
        var clean = new ItemStack(natural, 200);
        object?[] pourArgs = [world, mixed, clean, 2f, 4f, 0];
        var poured = (ItemStack)mixture.Invoke(null, pourArgs)!;
        Require((int)pourArgs[5]! == 100 && poured.StackSize == 400 && clean.StackSize == 200 && mixed.StackSize == 300,
            "Partial pour did not respect capacity or mutated source stacks.");
        loads = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(poured.Attributes.GetString("vsslssmod:pathogens-v1"))!;
        Require(Math.Abs(loads["shigella"] * 4000 - 1000) < 1e-9, "Pouring lost pathogen dose.");
        clean.Attributes.SetString("foreign:owner", "other");
        Require(mixture.Invoke(null, [world, mixed, clean, 2f, 50f, 0]) is null, "Foreign identity was erased during slurry pouring.");
        Require(mixture.Invoke(null, [world, new ItemStack(foreign), mixed, 2f, 50f, 0]) is null, "Slurry mixed into an unrelated liquid.");
        barrel.Inventory[1].Itemstack = null;
        airGap = 3; barrel.Pos = latrinePos.AddCopy(0, -4, 0);
        storage = Read() ?? throw new Exception("Barrel beneath an air gap was rejected.");
        obstructed = true; RejectAdd(storage, 1);
        Require(Read() is null, "Obstructed air gap accepted.");
        obstructed = false; Add(Read()!, 1);
        Require(barrel.Inventory[1].StackSize == 100, "Air-gap delivery failed.");
        Console.WriteLine("PASS latrine slurry: repeated use, capacity, legacy migration, optional fertilizer, rollback, mass/pathogens, pollution and bounded liquid mixing");
        var animations = new List<string>();
        var entity = new EntityAgent();
        entity.AnimManager = Proxy<IAnimationManager>((method, args) => method.Name switch {
            "StartAnimation" => Started(((AnimationMetaData)args![0]!).Code),
            "StopAnimation" => Stopped((string)args![0]!),
            _ => throw new Exception("Unexpected seat animation " + method.Name)
        });
        bool Started(string code) { animations.Add(code); return true; }
        object? Stopped(string code) { animations.Remove(code); return null; }
        var seat = new BlockEntityLatrine { Pos = latrinePos,
            Api = Proxy<ICoreAPI>((method, _) => method.Name == "get_Side" ? EnumAppSide.Client : throw new Exception(method.Name)) };
        seat.DidMount(entity);
        Require(seat.Controls.Sneak && seat.SuggestedAnimation.Code == "vsslssmod-squat"
            && animations.SequenceEqual(new[] { "vsslssmod-squat" }) && seat.LocalEyePos.Y < 1
            && seat.SuggestedAnimation.TriggeredBy is null && seat.SuggestedAnimation.SupressDefaultAnimation,
            $"Latrine retained a standing animation or eye height: sneak={seat.Controls.Sneak}, suggested={seat.SuggestedAnimation.Code}, animations={string.Join(',', animations)}, eye={seat.LocalEyePos.Y}.");
        seat.DidUnmount(entity);
        Require(animations.Count == 0 && !seat.AnyMounted(), "Unmounting retained the squat animation or occupant.");
        var patches = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/patches/player-squat.json"))).AsArray()!;
        Require(patches[0]["file"].AsString() == "game:shapes/entity/humanoid/seraph-faceless.json"
            && patches[0]["path"].AsString() == "/animations/-"
            && patches[1]["file"].AsString() == "game:entities/humanoid/player.json"
            && patches[1]["path"].AsString() == "/client/animations/-", "Squat patch targets the wrong native asset.");
        var animation = patches[0]["value"].AsObject<Animation>()!;
        var meta = patches[1]["value"].AsObject<AnimationMetaData>()!;
        Require(meta.Code == seat.SuggestedAnimation.Code && meta.Animation == animation.Code
            && meta.TriggeredBy is null && meta.Weight == seat.SuggestedAnimation.Weight
            && meta.BlendMode == seat.SuggestedAnimation.BlendMode && meta.SupressDefaultAnimation
            && meta.ElementWeight.OrderBy(k => k.Key).SequenceEqual(seat.SuggestedAnimation.ElementWeight.OrderBy(k => k.Key)),
            "Server/mount and client animation metadata disagree.");
        var shape = JsonObject.FromJson(File.ReadAllText(Path.Combine(gamePath,
            "assets/game/shapes/entity/humanoid/seraph-faceless.json"))).AsObject<Shape>()!;
        shape.Animations = [.. shape.Animations, animation];
        shape.InitForAnimations(null!, "seraph-squat-test");
        animation.GenerateAllFrames(shape.Elements, shape.JointsById);
        IEnumerable<ElementPose> Flatten(IEnumerable<ElementPose> poses) => poses.SelectMany(p => new[] { p }.Concat(Flatten(p.ChildElementPoses)));
        foreach (var frame in animation.PrevNextKeyFrameByFrame)
        {
            var poses = Flatten(frame[0].RootElementTransforms).ToDictionary(p => p.ForElement.Name!);
            Require(poses["LowerTorso"].translateY < -.5f
                && poses["UpperFootL"].degZ < -100 && poses["UpperFootR"].degZ < -100
                && poses["LowerFootL"].degZ > 120 && poses["LowerFootR"].degZ > 120,
                "Native player animation did not resolve a lowered pelvis and deeply bent legs.");
        }
        // Exercise ground-pose ownership on the actual server session helpers, including cancellation/death.
        var player = new PosePlayer { Alive = true, AnimManager = entity.AnimManager };
        var serverPlayer = FixturePlayer.Make(player, null!);
        var coordinator = typeof(BlockLatrine).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var sessionType = coordinator.GetNestedType("Session", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, hidden | BindingFlags.Public, null, [serverPlayer], null)!;
        var pendingType = coordinator.GetNestedType("PendingDefecation", BindingFlags.NonPublic)!;
        var pending = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pendingType);
        var defecation = sessionType.GetProperty("Defecation")!;
        var update = coordinator.GetMethod("UpdateGroundSquat", hidden)!;
        var clear = coordinator.GetMethod("ClearGroundSquat", hidden)!;
        defecation.SetValue(session, pending); update.Invoke(null, [session]); update.Invoke(null, [session]);
        Require(animations.SequenceEqual(new[] { meta.Code }), $"Ground squat failed: animations={string.Join(',', animations)}, alive={player.Alive}, manager={player.AnimManager is not null}, stored={sessionType.GetProperty("GroundSquattingEntity")!.GetValue(session) is not null}.");
        defecation.SetValue(session, null); update.Invoke(null, [session]);
        Require(animations.Count == 0, "Ground completion/cancellation retained the pose.");
        defecation.SetValue(session, pending); update.Invoke(null, [session]); player.Alive = false; update.Invoke(null, [session]);
        Require(animations.Count == 0, "Death retained the ground squat.");
        player.Alive = true; update.Invoke(null, [session]); clear.Invoke(null, [session]); clear.Invoke(null, [session]);
        Require(animations.Count == 0, "Lifecycle cleanup retained the squat or failed when called twice.");
        Console.WriteLine("PASS dedicated squat resolves on the native player skeleton; latrine/ground pose ownership, cancellation, death and cleanup");
        Console.WriteLine("PASS actual native barrel requirement, manure inventory/texture, capacity, Natural Fertilizer preference, stale-action rejection and rollback");
    }
}


