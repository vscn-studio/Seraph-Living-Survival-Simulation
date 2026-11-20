using System.Reflection;
using System.Text.Json;
using Seraph;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class FixtureChecks
{
    internal static void Run(string root)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var master = new BlockBathtub { Code = new("vsslssmod:bathtub"), BlockId = 50 };
        var part = new BlockBathtub { Code = new("vsslssmod:bathtub-part"), BlockId = 51 };
        var latrine = new BlockLatrine { Code = new("vsslssmod:latrine"), BlockId = 52 };
        var pos = new BlockPos(10, 20, 30, 0);
        var partPos = pos.AddCopy(1, 0, 0);
        var bath = new BlockEntityBathtub { Block = master, Pos = pos };
        var companion = new BlockEntityBathtub { Block = part, Pos = partPos };
        var water = new Item { Code = new("game:waterportion"), ItemId = 100,
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}") };
        var bucket = new BlockBucket { Code = new("game:bucket-wood"), BlockId = 60,
            Attributes = JsonObject.FromJson("{\"handleLiquidContainerInteract\":true}") };
        var barrel = new BlockBarrel { Code = new("game:barrel-oak"), BlockId = 61 };
        typeof(BlockLiquidContainerTopOpened).GetField("Props", all)!.SetValue(bucket, new LiquidTopOpenContainerProps { CapacityLitres = 10 });
        typeof(BlockLiquidContainerBase).GetField("capacityLitresFromAttributes", all)!.SetValue(barrel, 50f);
        var slot = new DummySlot();
        ItemStack? given = null;
        bool Give(ItemStack stack) { given = stack; return true; }
        var inventory = Proxy<IPlayerInventoryManager>((method, args) => method.Name switch
        {
            "get_ActiveHotbarSlot" => slot, "get_InventoriesOrdered" => Array.Empty<InventoryBase>(),
            "TryGiveItemstack" => Give((ItemStack)args![0]!),
            _ => throw new Exception("Unexpected inventory call " + method.Name)
        });
        var entity = new EntityPlayer { SelectionBox = new Cuboidf(0, 0, 0, 1, 2, 1) };
        var player = FixturePlayer.Make(entity, inventory);
        player.WorldData = Proxy<IWorldPlayerData>((method, _) => method.Name == "get_EntityControls" ? entity.Controls : null);
        var access = true;
        var claimed = new List<BlockPos>();
        var claims = Proxy<ILandClaimAPI>((method, args) => method.Name == "TryAccess" ? Access((BlockPos)args![1]!) : throw new Exception(method.Name));
        bool Access(BlockPos target) { claimed.Add(target.Copy()); return access; }
        var targetBlock = (Block?)null;
        var barrelEntity = new BlockEntityBarrel { Block = barrel, Pos = pos };
        var side = EnumAppSide.Server;
        var accessor = Proxy<IBlockAccessor>((method, args) => method.Name switch
        {
            "GetBlock" => targetBlock ?? (args![0] is BlockPos { X: 11 } ? part : master),
            "GetBlockEntity" => ReferenceEquals(targetBlock, barrel) ? barrelEntity : ((BlockPos)args![0]!).X == 11 ? companion : bath,
            "GetClimateAt" or "GetDecors" or "GetChunkAtBlockPos" => null, "MarkBlockEntityDirty" or "MarkBlockDirty" => null,
            _ => throw new Exception("Unexpected accessor call " + method.Name)
        });
        var calendar = Proxy<IGameCalendar>((method, _) => method.Name == "get_TotalHours" ? 10d
            : throw new Exception("Unexpected calendar call " + method.Name));
        var world = Proxy<IServerWorldAccessor>((method, args) => method.Name switch
        {
            "get_BlockAccessor" => accessor, "GetItem" => water, "get_Claims" => claims, "get_Side" => side,
            "GetBlock" => args![0] is int id && id == 60 ? bucket : master,
            "PlayerByUid" => player,
            "get_Calendar" => calendar,
            "get_Blocks" => new Block[] { bucket, barrel },
            "PlaySoundAt" or "SpawnCubeParticles" => null,
            _ => throw new Exception("Unexpected world call " + method.Name)
        });
        entity.World = world;
        var modSystem = new SeraphModSystem();
        var recipes = new RecipeRegistrySystem();
        var loader = Proxy<IModLoader>((method, _) => method.Name == "GetModSystem" ?
            method.GetGenericArguments()[0] == typeof(RecipeRegistrySystem) ? recipes : modSystem
            : throw new Exception("Unexpected mod loader call " + method.Name));
        var core = Proxy<ICoreServerAPI>((method, _) => method.Name switch
        {
            "get_World" => world, "get_ModLoader" => loader, "get_Side" => side,
            _ => throw new Exception("Unexpected API call " + method.Name)
        });
        typeof(SeraphModSystem).GetField("server", all)!.SetValue(modSystem,
            Activator.CreateInstance(typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!, [core]));
        bath.Api = core; companion.Api = core; barrelEntity.Api = core; barrelEntity.Inventory.Api = core;
        using var transfers = (IDisposable)Activator.CreateInstance(
            typeof(BlockBathtub).Assembly.GetType("Seraph.World.NativeWaterTransfer", true)!, all, null, [core], null)!;
        typeof(CollectibleObject).GetField("api", all)!.SetValue(bucket, core);
        typeof(CollectibleObject).GetField("api", all)!.SetValue(barrel, core);
        typeof(CollectibleObject).GetField("api", all)!.SetValue(water, core);
        foreach (var locale in new[] { "en", "zh-cn" })
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(root, "assets/vsslssmod/lang", locale + ".json")))!;
            var translation = new TranslationService(locale, null!);
            typeof(TranslationService).GetField("entryCache", all)!.SetValue(translation,
                entries.ToDictionary(p => "vsslssmod:" + p.Key, p => p.Value));
            typeof(TranslationService).GetField("loaded", all)!.SetValue(translation, true);
            Lang.AvailableLanguages[locale] = translation;
            Lang.ChangeLanguage(locale);
            foreach (var block in new Block[] { master, part, latrine })
            {
                var expected = ReferenceEquals(block, latrine) ? (locale == "en" ? "Latrine" : "旱厕")
                    : (locale == "en" ? "Bathtub" : "浴桶");
                Require(block.GetHeldItemName(new ItemStack(block)) == expected, "Held fixture name missing: " + expected);
                Require(block.GetPlacedBlockName(world, pos) == expected, "Placed fixture name missing: " + expected);
            }
        }
        var a = master.GetSelectionBoxes(accessor, pos);
        var b = part.GetSelectionBoxes(accessor, partPos);
        Require(a.Length == 1 && b.Length == 1 && a[0].X1 == b[0].X1 + 1 && a[0].X2 == b[0].X2 + 1,
            "Bath halves do not select the same world bounds.");
        Require(a[0].X1 == 0 && a[0].X2 == 2 && a[0].Y1 == 0 && a[0].Y2 == 1
            && a[0].Z1 == 0 && a[0].Z2 == 1, "Bath selection is not two full cells.");
        var toiletSelection = latrine.GetSelectionBoxes(accessor, pos).Single();
        Require(toiletSelection.X1 == 0 && toiletSelection.X2 == 1 && toiletSelection.Z1 == 0
            && toiletSelection.Z2 == 1 && toiletSelection.Y1 == 0 && toiletSelection.Y2 == .125f,
            "Latrine selection does not cover its footprint at the actual microblock height.");
        Require(master.GetCollisionBoxes(accessor, pos).Length > 1, "Selection replaced physical wall collisions.");
        Require(master.GetDrops(world, pos, player).Single().Block == master
            && part.GetDrops(world, partPos, player).Single().Block == master, "Companion drops a different item.");
        void FillBucket(int count)
        {
            var held = new ItemStack(bucket, count);
            var content = new ItemStack(water, 1000);
            var metadata = typeof(BlockBathtub).Assembly.GetType("Seraph.World.WaterMetadata", true)!;
            metadata.GetMethod("Write")!.Invoke(null, [content, new WaterBatch { VolumeL = 10, TemperatureC = 32,
                SaltGramsPerL = 2, EntericHazardPerL = .1, Turbidity = .25, Source = "surface" }]);
            bucket.SetContent(held, content);
            slot.Itemstack = held;
        }
        void Interact(Block block, BlockPos target)
        {
            var handling = EnumHandHandling.NotHandled;
            bucket.OnHeldInteractStart(slot, entity, new BlockSelection { Position = target }, null, true, ref handling);
            Require(handling == EnumHandHandling.PreventDefault, "Held bucket swallowed the bath interaction.");
        }
        FillBucket(1); Interact(master, pos);
        Require(bath.WaterLitres == 10 && bucket.GetContent(slot.Itemstack!) is null,
            "Plain right-click did not transfer one wooden bucket.");
        FillBucket(1); Interact(part, partPos);
        Require(bath.WaterLitres == 20 && companion.WaterLitres == 0, "Companion used a separate reservoir.");
        FillBucket(2); Interact(part, partPos);
        Require(bath.WaterLitres == 30 && given is not null && bucket.GetContent(given) is null && slot.StackSize == 1,
            "Stacked buckets did not split exactly one container.");
        var stored = (WaterBatch)typeof(BlockEntityBathtub).GetField("water", all)!.GetValue(bath)!;
        Require(stored.TemperatureC == 32 && stored.SaltGramsPerL == 2 && stored.Turbidity == .25,
            "Pouring lost water quality metadata.");
        var bathInfo = master.GetPlacedBlockInfo(world, pos, player);
        Require(bathInfo == part.GetPlacedBlockInfo(world, partPos, player)
            && bathInfo.Contains("水温：32 ℃") && bathInfo.Contains("盐度：2 g/L")
            && bathInfo.Contains("饮用建议") && bathInfo.Contains("肠道病原暴露强度")
            && !bathInfo.Contains("可能病原体") && bathInfo.Contains("水质风险：高"),
            "Bath halves lost shared water quality or displayed a speculative drinking risk: " + bathInfo);
        typeof(BlockEntityBathtub).GetField("water", all)!.SetValue(bath, stored with { VolumeL = 195 });
        FillBucket(1); Interact(master, pos);
        Require(bath.WaterLitres == 200 && bucket.GetContent(slot.Itemstack!)?.StackSize == 500,
            "Near-full bath lost or duplicated excess water.");
        Interact(master, pos);
        Require(bucket.GetContent(slot.Itemstack!)?.StackSize == 500, "Full bath consumed more water.");
        slot.Itemstack = new ItemStack(bucket, 1);
        Interact(master, pos);
        Require(bath.WaterLitres == 190 && bucket.GetContent(slot.Itemstack!)?.StackSize == 1000,
            "Empty wooden bucket did not recover 10 L of bath water.");
        var remainingWater = (WaterBatch)typeof(BlockEntityBathtub).GetField("water", all)!.GetValue(bath)!;
        Require(remainingWater.Turbidity == stored.Turbidity && remainingWater.EntericHazardPerL == stored.EntericHazardPerL,
            "Ordinary withdrawal polluted the remaining bath water.");
        Require(claimed.Any(p => p.Equals(pos)) && claimed.Any(p => p.Equals(partPos)), "Bath did not check both cell claims.");
        FillBucket(1); access = false;
        Interact(part, partPos);
        Require(bath.WaterLitres == 190 && bucket.GetContent(slot.Itemstack!)?.StackSize == 1000, "Claimed bath accepted water.");
        Require(master.OnBlockInteractStart(world, player, new() { Position = pos }), "Direct bath interaction unhandled.");
        Require(bath.WaterLitres == 190, "Direct block entry bypassed bath claims.");
        access = true; side = EnumAppSide.Client;
        Interact(part, partPos);
        Require(bath.WaterLitres == 190 && bucket.GetContent(slot.Itemstack!)?.StackSize == 1000, "Client predicted a destructive water transfer.");
        side = EnumAppSide.Server;
        // The native server start packet sets firstEvent=false; it is not a held-step replay.
        var serverStart = EnumHandHandling.NotHandled;
        bucket.OnHeldInteractStart(slot, entity, new() { Position = pos }, null, false, ref serverStart);
        Require(bath.WaterLitres == 200 && bucket.GetContent(slot.Itemstack!) is null
            && serverStart == EnumHandHandling.PreventDefault, "Native server start packet failed to transfer one bucket.");
        var route = transfers.GetType().GetMethod("BathtubInteract", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] routeArgs = [bucket, entity, slot, new BlockSelection { Position = pos }, true, EnumHandHandling.NotHandled];
        targetBlock = new Block();
        Require((bool)route.Invoke(null, routeArgs)!, "Bath route intercepted an unrelated block.");
        targetBlock = null; entity.Controls.ShiftKey = true;
        Require((bool)route.Invoke(null, routeArgs)!, "Bath route intercepted native Shift placement.");
        entity.Controls.ShiftKey = false;
        transfers.GetType().GetMethod("RegisterBlocks")!.Invoke(transfers, null);
        targetBlock = barrel;
        barrelEntity.Inventory[1].Itemstack = new ItemStack(water, 1000);
        FillBucket(1);
        var barrelHandling = EnumHandHandling.NotHandled;
        bucket.OnHeldInteractStart(slot, entity, new BlockSelection { Position = pos }, null, false, ref barrelHandling);
        Require(barrelHandling == EnumHandHandling.PreventDefault && barrel.GetContent(pos)?.StackSize == 2000
            && bucket.GetContent(slot.Itemstack!) is null, "Second bucket did not pour into the placed barrel.");
        FillBucket(1);
        barrelEntity.Inventory[1].Itemstack!.StackSize = 4500;
        bucket.OnHeldInteractStart(slot, entity, new BlockSelection { Position = pos }, null, false, ref barrelHandling);
        Require(barrel.GetContent(pos)?.StackSize == 5000 && bucket.GetContent(slot.Itemstack!)?.StackSize == 500,
            "Nearly full barrel did not preserve the remaining bucket water.");
        barrelEntity.Inventory[1].Itemstack!.StackSize = 1000;
        FillBucket(1); side = EnumAppSide.Client;
        bucket.OnHeldInteractStart(slot, entity, new BlockSelection { Position = pos }, null, true, ref barrelHandling);
        Require(barrel.GetContent(pos)?.StackSize == 1000 && bucket.GetContent(slot.Itemstack!)?.StackSize == 1000,
            "Client predicted a barrel transfer.");
        side = EnumAppSide.Server; access = false;
        bucket.OnHeldInteractStart(slot, entity, new BlockSelection { Position = pos }, null, false, ref barrelHandling);
        Require(barrel.GetContent(pos)?.StackSize == 1000 && bucket.GetContent(slot.Itemstack!)?.StackSize == 1000,
            "Barrel transfer bypassed claims.");
        access = true;
        Console.WriteLine("PASS actual native held-bucket bath entry, both halves, stacked containers, quality/capacity conservation, claims, client isolation, server start packet and native fallback");
    }
}
