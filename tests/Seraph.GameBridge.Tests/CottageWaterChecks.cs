using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Seraph;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class CottageWaterChecks
{
    internal static void Run(string assemblyPath)
    {
        BedRestChecks.InitializeTranslations();
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var vatType = assembly.GetType("CottageIndustry.BETreadingVat", true)!;
        var mod = new SeraphModSystem();
        var cottage = Activator.CreateInstance(assembly.GetType("CottageIndustry.CottageIndustrySystem", true)!)!;
        var cfg = cottage.GetType().GetField("Config")!.GetValue(cottage)!;
        cfg.GetType().GetField("TubSoakLitres")!.SetValue(cfg, 20d);
        cfg.GetType().GetField("LagarSoakLitres")!.SetValue(cfg, 20d);
        var side = EnumAppSide.Server;
        var hours = 0d;
        var now = 1000L;
        var allow = true;
        var worldCache = new Dictionary<string, object>();
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? mod : null);
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? hours : null);
        var water = Liquid("waterportion", 100);
        var saltwater = Liquid("saltwaterportion", 101);
        var juice = Liquid("juice-apple", 102);
        var solid = new Item { Code = new("game:soak-input"), ItemId = 103, MaxStackSize = 64 };
        var soaked = new Item { Code = new("game:soak-output"), ItemId = 104, MaxStackSize = 64 };
        var bucket = new TestBucket { BlockId = 20, Code = new("game:bucket-wood") };
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? allow : null);
        var events = Proxy<IServerEventAPI>((m, _) => m.Name == "RegisterGameTickListener" ? 1L : null);
        BlockEntity vat = null!;
        var accessor = Proxy<IBlockAccessor>((m, _) => m.Name switch
        {
            "GetClimateAt" => new ClimateCondition { Temperature = 15 },
            "GetBlockEntity" => vat, _ => null
        });
        ICoreAPI api = null!;
        var world = Proxy<IWorldAccessor>((m, a) => m.Name switch
        {
            "get_Api" => api, "get_Side" => side, "get_Calendar" => calendar, "get_ElapsedMilliseconds" => now,
            "get_BlockAccessor" => accessor, "get_Claims" => claims, "get_Blocks" => new Block[] { bucket },
            "GetItem" => a![0] is int id ? id == 100 ? water : id == 101 ? saltwater : juice
                : ((AssetLocation)a![0]!).Path == "waterportion" ? water : ((AssetLocation)a[0]!).Path == "saltwaterportion" ? saltwater : juice,
            _ => null
        });
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => side, "get_ModLoader" => loader, "get_ObjectCache" => worldCache,
            "get_Event" => events, _ => null
        });
        AccessTools.Field(typeof(SeraphModSystem), "server").SetValue(mod, new Seraph.Server.ServerCoordinator((ICoreServerAPI)api));
        foreach (var item in new CollectibleObject[] { water, saltwater, juice, solid, soaked, bucket }) AccessTools.Field(typeof(CollectibleObject), "api").SetValue(item, api);
        using var native = new NativeWaterTransfer(api);
        native.RegisterBlocks();
        var slot = new DummySlot();
        var returned = new List<ItemStack>();
        var inventory = Proxy<IPlayerInventoryManager>((m, a) =>
        {
            if (m.Name == "TryGiveItemstack") { returned.Add(((ItemStack)a![0]!).Clone()); return true; }
            return m.Name == "get_ActiveHotbarSlot" ? slot : null;
        });
        var player = FixturePlayer.Make(new EntityPlayer(), inventory);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : null);
        var interact = AccessTools.Method(vatType, "SoakInteract");
        var accept = AccessTools.Method(vatType, "AcceptPoured");
        var rain = AccessTools.Method(vatType, "AddRain");
        var contentsField = vatType.GetField("C")!;
        var juiceField = contentsField.FieldType.GetField("Juice")!;
        ItemStack? Current() => (ItemStack?)juiceField.GetValue(contentsField.GetValue(vat));
        void Store(ItemStack? stack) => juiceField.SetValue(contentsField.GetValue(vat), stack);
        WaterBatch Read(ItemStack? stack) => WaterQuality.ReadLiquidAt(world, stack, 15, mod.InteractionConfig)!;
        ItemStack Batch(int count, double temperature, double hazard, double salt = 0)
        {
            var stack = new ItemStack(salt > 5 ? saltwater : water, count);
            WaterMetadata.WriteAt(world, stack, new WaterBatch { TemperatureC = temperature, EntericHazardPerL = hazard,
                SaltGramsPerL = salt, Turbidity = hazard / 10, PathogensPerL = new Dictionary<string, double> { ["shigella"] = hazard / 2 } });
            return stack;
        }
        void Hold(ItemStack? liquid, int containers = 1)
        {
            slot.Itemstack = new ItemStack(bucket, containers);
            bucket.SetContent(slot.Itemstack, liquid!);
        }
        bool Pour() => (bool)interact.Invoke(vat, [player, slot])!;
        int Stream(ItemStack source, float litres) => (int)accept.Invoke(vat, [source, litres])!;
        void NewVat(bool stone)
        {
            vat = (BlockEntity)Activator.CreateInstance(vatType)!;
            vat.Api = api; vat.Pos = new BlockPos(1, 2, 3, 0);
            vat.Block = (Block)Activator.CreateInstance(assembly.GetType("CottageIndustry.BlockTreadingVat", true)!)!;
            vat.Block.Code = new AssetLocation("cottageindustry", stone ? "lagar" : "treadingtub");
            AccessTools.Property(vat.Block.GetType(), "IsStone").SetValue(vat.Block, stone);
            AccessTools.Field(vatType, "modSys").SetValue(vat, cottage);
        }

        NewVat(false);
        Store(Batch(1000, 10, 0)); Hold(Batch(1000, 70, 2));
        Pour();
        Require(Current()!.StackSize == 1000 && player.IngameErrors.Last().Code == "vat-nomix",
            "Test must reproduce the installed mod's water rejection before compatibility is attached.");
        using var adapter = new CottageWaterCompatibility(vatType);
        // Client/server owners may coexist in single player; disposing one must
        // not remove the other's patches.
        using (var secondOwner = new CottageWaterCompatibility(vatType)) { }
        foreach (var stone in new[] { false, true })
        {
            NewVat(stone);
            Hold(Batch(1000, 10, 0)); Require(Pour(), "First pour failed.");
            Hold(Batch(1000, 70, 2, 2)); Require(Pour(), "Repeated pour failed.");
            Require(Current()!.StackSize == 2000 && bucket.GetContent(slot.Itemstack!) is null, "Pour duplicated/lost water.");
            Near(Read(Current()).TemperatureC, 40, "Temperature mixing");
            Near(Read(Current()).EntericHazardPerL, 1, "Generic contamination mixing");
            Near(Read(Current()).PathogensPerL["shigella"], .5, "Named pathogen mixing");
            Near(Read(Current()).SaltGramsPerL, 1, "Salt mixing");
            Near(Read(Current()).Turbidity, .1, "Turbidity mixing");
            Hold(Batch(1000, 90, 3)); Pour(); Require(bucket.GetContent(slot.Itemstack!)!.StackSize == 1000, "Full vat consumed source water.");
            Hold(null); Require(Pour(), "Drawing failed.");
            Require(Current()!.StackSize == 1000 && bucket.GetContent(slot.Itemstack!)!.StackSize == 1000, "Drawing changed total volume.");
            Near(Read(bucket.GetContent(slot.Itemstack!)).SaltGramsPerL, 1, "Drawing lost salt");
            Require(Pour() && Current()!.StackSize == 2000, "Pour-back failed.");

            var saved = new TreeAttribute(); vat.ToTreeAttributes(saved);
            var bytes = saved.ToBytes(); var restored = new TreeAttribute(); restored.FromBytes(bytes);
            NewVat(stone); vat.FromTreeAttributes(restored, world);
            Require(Current()!.StackSize == 2000, "Native save/load lost volume.");
            Near(Read(Current()).SaltGramsPerL, 1, "Native save/load lost quality");

            Store(Batch(1950, 15, 1)); Hold(Batch(1000, 15, 2), 3); returned.Clear(); Pour();
            Require(Current()!.StackSize == 2000 && slot.StackSize == 2 && returned.Count == 1
                && bucket.GetContent(returned[0])!.StackSize == 950, "Partial stacked-vessel pour lost water or containers.");
            Store(Batch(1000, 15, 1)); Hold(null, 3); returned.Clear(); Pour();
            Require(Current() is null && slot.StackSize == 2 && returned.Count == 1
                && bucket.GetContent(returned[0])!.StackSize == 1000, "Stacked empty vessels duplicated water.");

            Store(Batch(1000, 10, 2, 2)); now = 1001; var source = Batch(100, 50, 0);
            Require(Stream(source, 10) == 100 && source.StackSize == 100, "Stream exceeded its source or consumed it twice.");
            Near(Read(Current()).SaltGramsPerL, 20d / 11, "Stream salt conservation");
            Require(Stream(source, .5f) == 50 && Current()!.StackSize == 1150, "Throttled second stream failed.");
            Near(Read(Current()).SaltGramsPerL, 20d / 11.5, "Throttled stream used stale quality");
            foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -1f, 0f }) Require(Stream(source, invalid) == 0, "Invalid stream quantity accepted.");

            Store(Batch(1000, 15, 2, 2)); rain.Invoke(vat, [10d]);
            Require(Current()!.StackSize == 2000, "Rain volume incorrect.");
            Near(Read(Current()).SaltGramsPerL, 1, "Rain created/destroyed salt");
            Near(Read(Current()).EntericHazardPerL, (2 + mod.InteractionConfig.RainWaterHazardPerL) / 2, "Rain quality incorrect");
            Near(Read(Current()).PathogensPerL["shigella"], .5, "Rain lost named pathogen load");
            Store(null); rain.Invoke(vat, [5d]); Require(WaterMetadata.HasTag(Current()!), "Empty vat rain has no quality.");

            Store(Batch(500, 15, 0)); Hold(Batch(500, 15, 0, 35)); Pour();
            Require(Current()!.Collectible == saltwater, "Salty mix retained freshwater identity.");
            Near(Read(Current()).SaltGramsPerL, 17.5, "Salt/freshwater mixing");
            Store(Batch(500, 15, 1)); var foreign = Batch(100, 15, 1); foreign.Attributes.SetString("other:identity", "different");
            Hold(foreign); Pour(); Require(Current()!.StackSize == 500 && bucket.GetContent(slot.Itemstack!)!.StackSize == 100, "Unknown identity was ignored.");
            var invalidWater = Batch(100, 15, 1); invalidWater.Attributes.SetString(WaterMetadata.Key, "broken");
            Hold(invalidWater); Pour(); Require(Current()!.StackSize == 500 && Stream(invalidWater, 1) == 0, "Malformed metadata was consumed.");
            Store(invalidWater); Hold(Batch(100, 15, 1)); Pour(); Require(Current()!.StackSize == 100
                && Current()!.Attributes.GetString(WaterMetadata.Key) == "broken", "Malformed stored water was overwritten.");
            Store(Batch(100, 15, 1)); Hold(Batch(100, 15, 0)); allow = false;
            Require(!Pour() && Current()!.StackSize == 100, "Claim-denied water transfer succeeded."); allow = true;
            side = EnumAppSide.Client; Require(Pour() && Current()!.StackSize == 100 && Stream(source, 1) == 0, "Client mutated reservoir water."); side = EnumAppSide.Server;
            Store(new ItemStack(juice, 100)); Hold(Batch(100, 15, 0)); Pour(); Require(Current()!.Collectible == juice && Current()!.StackSize == 100, "Water mixed with juice.");
            Hold(new ItemStack(juice, 100)); Pour(); Require(Current()!.StackSize == 200, "Native juice transfer changed.");

            Store(new ItemStack(water, 1000)); Hold(Batch(1000, 15, 0)); Pour();
            Require(Current()!.StackSize == 2000, "Existing untagged vat cannot receive tagged water.");
            Near(Read(Current()).EntericHazardPerL, mod.InteractionConfig.FreshWaterHazardPerL / 2, "Old water lost baseline risk");

            // Exercise the installed vat's own planning, timer and recipe debit
            // after a water-quality merge, using a small deterministic recipe.
            var recipe = new BarrelRecipe { Code = "test-soaking", SealHours = 1,
                Ingredients = [
                    new BarrelRecipeIngredient { Type = EnumItemClass.Item, Code = solid.Code, Quantity = 1, ResolvedItemStack = new ItemStack(solid) },
                    new BarrelRecipeIngredient { Type = EnumItemClass.Item, Code = water.Code, Litres = 1, ConsumeLitres = 1, ResolvedItemStack = new ItemStack(water, 100) }
                ],
                Output = new BarrelOutputStack { Type = EnumItemClass.Item, Code = soaked.Code, StackSize = 1, ResolvedItemStack = new ItemStack(soaked) }
            };
            AccessTools.Field(vatType, "serverSoakRecipes").SetValue(null, new List<BarrelRecipe> { recipe });
            AccessTools.Field(vatType, "Soaking").SetValue(vat, new ItemStack(solid));
            Store(Batch(100, 15, 2)); Hold(Batch(100, 15, 0)); Pour();
            Require(AccessTools.Field(vatType, "SoakRecipe").GetValue(vat) is not null && Current()!.StackSize == 200,
                "Water merge broke the native soaking plan or consumed water early.");
            hours += 1;
            AccessTools.Method(vatType, "CheckSoak").Invoke(vat, null);
            Require(((ItemStack)AccessTools.Field(vatType, "Soaking").GetValue(vat)!).Collectible == soaked
                && Current()!.StackSize == 100, "Native timed recipe lost its output or water accounting.");
            Near(Read(Current()).EntericHazardPerL, 1, "Soaking changed leftover water quality");
            AccessTools.Field(vatType, "Soaking").SetValue(vat, null);

            foreach (var mode in new[] { "shadow", "observe" })
            {
                AccessTools.Property(mod.InteractionConfig.GetType(), "Mode").SetValue(mod.InteractionConfig, mode);
                Store(Batch(100, 15, 1)); Hold(Batch(100, 15, 0)); Pour();
                Require(Current()!.StackSize == 100, "Compatibility changed native behavior in " + mode);
            }
            AccessTools.Property(mod.InteractionConfig.GetType(), "Mode").SetValue(mod.InteractionConfig, "survival");
        }
        Require(AccessTools.Field(typeof(CottageWaterCompatibility), "active").GetValue(null) is null, "Transfer context leaked.");
        Console.WriteLine("PASS installed Cottage Industry 1.2.3 wood/stone vats: repeated pours, draws, partial stacked vessels, streams, rain, metadata persistence, salt/pathogens/heat conservation, malformed data, claims and client ownership");
    }

    private static Item Liquid(string path, int id) => new()
    {
        Code = new AssetLocation("game", path), ItemId = id, MaxStackSize = 1000,
        Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}")
    };
    private sealed class TestBucket : BlockLiquidContainerTopOpened { public override float CapacityLitres => 10; }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < 1e-6, message + $": {actual} != {expected}");
}
