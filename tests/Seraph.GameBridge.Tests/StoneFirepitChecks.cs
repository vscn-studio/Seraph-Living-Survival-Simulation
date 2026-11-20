using System.Reflection;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;

internal static class StoneFirepitChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var patch = typeof(NativeStoneFirepit);
        using var bridge = new NativeStoneFirepit();
        var pit = new BlockEntityFirepit();
        var read = patch.GetMethod("ReadState", flags)!;
        var write = patch.GetMethod("WriteState", flags)!;
        var burnDuration = patch.GetMethod("BurnDuration", flags)!;
        var interact = patch.GetMethod("Interact", flags)!;
        var isLooseStone = patch.GetMethod("IsLooseStone", flags)!;

        if (interact.ReturnType != typeof(bool))
            throw new Exception("The firepit prefix must control whether vanilla interaction continues.");
        var firepitPatch = typeof(BlockFirepit).GetMethod("OnBlockInteractStart")!;
        var harmonyType = typeof(HarmonyLib.Harmony);
        var harmony = harmonyType.GetMethod("GetPatchInfo")!.Invoke(null, [firepitPatch]);
        if (harmony is null || !((IEnumerable<string>)harmony.GetType().GetProperty("Owners")!.GetValue(harmony)!)
                .Contains("vsslssmod.stonefirepit"))
            throw new Exception("The stone firepit must intercept native right-click before the vanilla GUI opens.");
        object?[] noMatch = [null, null, null, null, false];
        if (!(bool)interact.Invoke(null, noMatch)! || (bool)noMatch[4]!)
            throw new Exception("Unmatched firepit interactions must continue into vanilla behavior.");

        bool Loose(string code, int count = 1) => (bool)isLooseStone.Invoke(null,
            [new ItemStack(new Vintagestory.API.Common.Item { Code = new(code) }, count)])!;
        if (!Loose("game:rock-granite") || !Loose("game:stone-granite")
            || Loose("game:cobblestone-granite") || Loose("game:rock-granite", 0))
            throw new Exception("Loose rock or stone item recognition is incorrect.");

        var partial = new TreeAttribute();
        partial.SetInt("vsslssmodStoneRingStones", 11);
        read.Invoke(null, [pit, partial]);
        object?[] duration = [pit, 1f];
        burnDuration.Invoke(null, duration);
        if ((float)duration[1]! != 1f) throw new Exception("An incomplete stone ring increased fuel duration.");

        var complete = new TreeAttribute();
        complete.SetInt("vsslssmodStoneRingStones", 12);
        read.Invoke(null, [pit, complete]);
        duration = [pit, 1f];
        burnDuration.Invoke(null, duration);
        if (Math.Abs((float)duration[1]! - 1.3f) > 0.0001f)
            throw new Exception("A complete stone ring did not apply the 30% fuel bonus.");

        var saved = new TreeAttribute();
        write.Invoke(null, [pit, saved]);
        if (saved.GetInt("vsslssmodStoneRingStones") != 12)
            throw new Exception("Stone ring progress was not saved.");
        // This check also runs without the localization suite in --hot-water.
        var savedEnglish = Lang.AvailableLanguages.GetValueOrDefault("en");
        if (savedEnglish is null)
        {
            var translation = new TranslationService("en", null!, null!, EnumLinebreakBehavior.AfterWord);
            var translationFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TranslationService).GetField("entryCache", translationFlags)!.SetValue(translation,
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("assets/vsslssmod/lang/en.json"))!
                    .ToDictionary(p => "vsslssmod:" + p.Key, p => p.Value));
            typeof(TranslationService).GetField("loaded", translationFlags)!.SetValue(translation, true);
            Lang.AvailableLanguages["en"] = translation;
        }
        try { InteractionChecks(interact, read, write); }
        finally { if (savedEnglish is null) Lang.AvailableLanguages.Remove("en"); }
        RenderChecks();
        Console.WriteLine("PASS stone firepit progress persistence and 30% fuel-duration bonus");
    }

    private static void InteractionChecks(MethodInfo interact, MethodInfo read, MethodInfo write)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var block = new BlockFirepit { Code = new("game:firepit-cold") };
        var upgraded = new BlockFirepit { Code = new("vsslssmod:stonefirepit-cold"), BlockId = 25, Drops = [] };
        var pos = new BlockPos(10, 20, 30, 0);
        var pit = new BlockEntityFirepit { Block = block, Pos = pos };
        var slot = new DummySlot(new ItemStack(new Item { Code = new("game:stone-granite"), ItemId = 101 }, 16));
        var entity = new EntityPlayer();
        entity.Controls.ShiftKey = true;
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : null);
        var player = FixturePlayer.Make(entity, inventory);
        var mode = EnumGameMode.Survival;
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? mode : null);
        var allowed = true;
        var side = EnumAppSide.Server;
        var exchanges = 0;
        var redraws = 0;
        var stones = slot.Itemstack!.Item;
        var basalt = new Item { Code = new("game:stone-basalt"), ItemId = 103 };
        var assetAvailable = true;
        var droppedContents = new List<ItemStack>();
        var itemScans = 0;
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? allowed : null);
        var accessor = Proxy<IBlockAccessor>((m, _) =>
        {
            if (m.Name == "ExchangeBlock") { exchanges++; return null; }
            if (m.Name == "MarkBlockDirty") { redraws++; return null; }
            return m.Name == "GetBlockEntity" ? pit : m.Name == "GetBlock" ? pit.Block : null;
        });
        ICoreAPI api = null!;
        var world = Proxy<IWorldAccessor>((m, a) =>
        {
            if (m.Name == "SpawnItemEntity") { if (a![0] is ItemStack drop) droppedContents.Add(drop.Clone()); return null; }
            if (m.Name == "get_Items") { itemScans++; return new[] { stones, basalt }; }
            return m.Name switch
            {
                "get_Side" => side, "get_Api" => api, "get_Claims" => claims, "get_BlockAccessor" => accessor,
                "GetBlock" => assetAvailable ? upgraded : null,
                "GetItem" => ((AssetLocation)a![0]!).Path == "stone-basalt" ? basalt : stones, _ => null
            };
        });
        var logger = Proxy<ILogger>((_, _) => null);
        api = Proxy<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => side, "get_Logger" => logger, _ => null
        });
        pit.Api = api;
        pit.Inventory.Api = api;
        ((InventorySmelting)pit.Inventory).pos = pos;
        var selection = new BlockSelection { Position = pos };
        var progress = new TreeAttribute();
        var help = typeof(NativeStoneFirepit).GetMethod("AddInteractionHelp", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (var i = 0; i < 100; i++)
        {
            object?[] args = [block, world, selection, player, Array.Empty<WorldInteraction>()];
            help.Invoke(null, args);
            if (((WorldInteraction[])args[4]!).Length != 1) throw new Exception("Missing stone upgrade interaction hint.");
        }
        if (itemScans != 1) throw new Exception("Interaction hints repeatedly scanned the item registry.");

        // Call the patched native entry point, rather than only the private helper.
        // Falling through into vanilla would try to open the firepit inventory.
        pit.Inventory[0].Itemstack = new ItemStack(new Item { Code = new("game:firewood"), ItemId = 102 }, 7);
        pit.Inventory[1].Itemstack = new ItemStack(new Item { Code = new("game:redmeat-raw"), ItemId = 104 }, 3);
        var fuel = pit.Inventory[0].Itemstack;
        var food = pit.Inventory[1].Itemstack;
        if (!block.OnBlockInteractStart(world, player, selection))
            throw new Exception("Shift + right-click did not handle the loose stones.");
        write.Invoke(null, [pit, progress]);
        if (slot.StackSize != 4 || progress.GetInt("vsslssmodStoneRingStones") != 12
            || pit.Block != upgraded || exchanges != 1 || redraws != 1)
            throw new Exception("Upgrade must consume twelve stones with one block exchange and redraw.");
        if (!ReferenceEquals(pit.Inventory[0].Itemstack, fuel) || !ReferenceEquals(pit.Inventory[1].Itemstack, food))
            throw new Exception("Upgrade replaced native fuel or cooking inventory.");
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 4 || exchanges != 1 || redraws != 1 || player.SentMessages.Count != 1)
            throw new Exception("A completed stone ring consumed stones, rebuilt terrain or spammed completion messages.");
        var drops = upgraded.GetDrops(world, pos, player, 3);
        if (drops.Length != 1 || drops[0].StackSize != 12 || drops[0].Collectible != stones)
            throw new Exception("Breaking must refund twelve original stones without multiplying or duplicating native inventory.");

        pit.Block = block;
        read.Invoke(null, [pit, new TreeAttribute()]);
        block.OnBlockInteractStart(world, player, selection);
        write.Invoke(null, [pit, progress]);
        if (slot.StackSize != 4 || progress.GetInt("vsslssmodStoneRingStones") != 0 || exchanges != 1)
            throw new Exception("An insufficient stack was partially consumed or upgraded.");
        slot.Itemstack = new ItemStack(stones, 16);
        side = EnumAppSide.Client;
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 16 || exchanges != 1) throw new Exception("Client prediction consumed or exchanged blocks.");
        side = EnumAppSide.Server;
        allowed = false;
        if (block.OnBlockInteractStart(world, player, selection) || slot.StackSize != 16)
            throw new Exception("A denied land claim accepted stones.");
        allowed = true;
        typeof(BlockEntityFirepit).GetField("fuelBurnTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(pit, 10f);
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 16) throw new Exception("A burning firepit accepted stones.");
        pit.fuelBurnTime = 0;
        assetAvailable = false;
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 16 || exchanges != 1) throw new Exception("Missing block assets consumed stones.");
        assetAvailable = true;

        // Migrate a partially built old surround, then persist both material types.
        var partial = new TreeAttribute();
        partial.SetInt("vsslssmodStoneRingStones", 7);
        read.Invoke(null, [pit, partial]);
        slot.Itemstack = new ItemStack(basalt, 5);
        block.OnBlockInteractStart(world, player, selection);
        if (!slot.Empty) throw new Exception("Legacy surround did not consume exactly its five missing stones.");
        write.Invoke(null, [pit, progress]);
        read.Invoke(null, [pit, progress]);
        drops = upgraded.GetDrops(world, pos, player);
        if (drops.Length != 2 || drops.Single(d => d.Collectible == stones).StackSize != 7
            || drops.Single(d => d.Collectible == basalt).StackSize != 5)
            throw new Exception("Saved surround did not refund the legacy and new stone materials.");

        // Placing the registered block directly must also carry its bonus and refund.
        read.Invoke(null, [pit, new TreeAttribute()]);
        drops = upgraded.GetDrops(world, pos, player);
        if (drops.Sum(d => d.StackSize) != 12) throw new Exception("A directly placed surround has no stone refund.");
        pit.Block = block;
        read.Invoke(null, [pit, new TreeAttribute()]);
        slot.Itemstack = new ItemStack(stones, 12);
        mode = EnumGameMode.Creative;
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 12 || pit.Block != upgraded) throw new Exception("Creative upgrade consumed stones or failed.");
        mode = EnumGameMode.Survival;

        bool ContinuesNative()
        {
            object?[] args = [block, world, player, selection, false];
            return (bool)interact.Invoke(null, args)!;
        }
        entity.Controls.ShiftKey = false;
        if (!ContinuesNative()) throw new Exception("Normal right-click was intercepted.");
        entity.Controls.ShiftKey = true;
        slot.Itemstack = new ItemStack(new Item { Code = new("game:firewood"), ItemId = 102 });
        if (!ContinuesNative()) throw new Exception("Shift + right-click fuel insertion was intercepted.");
        pit.OnBlockBroken();
        if (droppedContents.Count != 2 || droppedContents.Sum(s => s.StackSize) != 10
            || droppedContents.Single(s => s.Collectible.Code.Path == "firewood").StackSize != 7
            || droppedContents.Single(s => s.Collectible.Code.Path == "redmeat-raw").StackSize != 3 || !pit.Inventory.Empty)
            throw new Exception("Native firepit destruction lost or duplicated its fuel and cooking contents.");
        Console.WriteLine("PASS atomic 12-stone upgrade, single redraw, native inventory preservation, material refunds, legacy migration, client/server ownership, claims, burning rejection and fuel passthrough");
    }

    private static void RenderChecks()
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var gameRoot = Directory.GetParent(Path.GetDirectoryName(typeof(BlockFirepit).Assembly.Location)!)!.FullName;
        var assetReads = 0;
        var assets = Proxy<IAssetManager>((m, args) =>
        {
            if (m.Name != "TryGet") throw new Exception(m.Name);
            var code = (AssetLocation)args![0]!;
            var path = code.Domain == "game" ? Path.Combine(gameRoot, "assets", "survival", code.Path)
                : Path.Combine("assets", code.Domain, code.Path);
            assetReads++;
            return new Asset(File.ReadAllBytes(path), code, null!);
        });
        var source = Proxy<ITexPositionSource>((m, _) => m.Name == "get_AtlasSize" ? new Size2i(1024, 1024)
            : new TextureAtlasPosition { x1 = 0, y1 = 0, x2 = 1, y2 = 1 });
        var nativeTesselator = new ShapeTesselator(null!, null!, null!, null!);
        var tessellations = 0;
        var tesselator = Proxy<ITesselatorAPI>((m, args) =>
        {
            if (m.Name != "TesselateShape") throw new Exception(m.Name);
            nativeTesselator.TesselateShape("stonefirepit", (Shape)args![1]!, out var mesh, source);
            args[2] = mesh;
            tessellations++;
            return null;
        });
        var block = new BlockFirepit { Code = new("vsslssmod:stonefirepit-cold"), BlockId = 25 };
        var world = Proxy<IClientWorldAccessor>((m, _) => m.Name == "GetBlock" ? block : null);
        var cache = new Dictionary<string, object>();
        var api = Proxy<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Assets" => assets, "get_Tesselator" => tesselator, "get_ObjectCache" => cache,
            _ => throw new Exception(m.Name)
        });
        var pit = new BlockEntityFirepit { Api = api, Block = block };
        var inventory = Shape.TryGet(api, "vsslssmod:shapes/block/stonefirepit.json");
        nativeTesselator.TesselateShape("inventory", inventory, out var inventoryMesh, source);
        if (inventoryMesh.VerticesCount == 0) throw new Exception("Stone firepit item has no model.");
        foreach (var burn in new[] { "cold", "lit", "extinct" })
            foreach (var content in new[] { "normal", "wide", "spit" })
            {
                var mesh = pit.getOrCreateMesh(burn, content);
                if (mesh.VerticesCount == 0 || mesh.xyz.Take(mesh.VerticesCount * 3).Any(v => !float.IsFinite(v)))
                    throw new Exception("Invalid stone firepit mesh for " + burn + "/" + content);
                var previousBuilds = tessellations;
                var previousReads = assetReads;
                for (var i = 0; i < 100; i++)
                    if (!ReferenceEquals(mesh, pit.getOrCreateMesh(burn, content)))
                        throw new Exception("Repeated firepit rendering rebuilt its cached mesh.");
                if (tessellations != previousBuilds || assetReads != previousReads)
                    throw new Exception("Cached firepit mesh still parsed or tessellated assets.");
            }
        Console.WriteLine("PASS real native tessellation of inventory and all nine firepit states; repeated rendering reuses cached meshes");
    }
}
