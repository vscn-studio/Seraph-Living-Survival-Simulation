using System.Reflection;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
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
        Console.WriteLine("PASS BP-style stone firepit progress persistence and 30% fuel-duration bonus");
    }

    private static void InteractionChecks(MethodInfo interact, MethodInfo read, MethodInfo write)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var block = new BlockFirepit { Code = new("game:firepit-cold") };
        var pos = new BlockPos(10, 20, 30, 0);
        var pit = new BlockEntityFirepit { Block = block, Pos = pos };
        var slot = new DummySlot(new ItemStack(new Item { Code = new("game:stone-granite"), ItemId = 101 }, 16));
        var entity = new EntityPlayer();
        entity.Controls.ShiftKey = true;
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : null);
        var player = FixturePlayer.Make(entity, inventory);
        var allowed = true;
        var side = EnumAppSide.Server;
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? allowed : null);
        var accessor = Proxy<IBlockAccessor>((m, _) => m.Name == "GetBlockEntity" ? pit : null);
        ICoreAPI api = null!;
        var world = Proxy<IWorldAccessor>((m, _) => m.Name switch
        {
            "get_Side" => side, "get_Api" => api, "get_Claims" => claims, "get_BlockAccessor" => accessor, _ => null
        });
        api = Proxy<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => side, _ => null
        });
        pit.Api = api;
        pit.Inventory.Api = api;
        var selection = new BlockSelection { Position = pos };
        var progress = new TreeAttribute();

        // Call the patched native entry point, rather than only the private helper.
        // Falling through into vanilla would try to open the firepit inventory.
        for (var i = 1; i <= 12; i++)
        {
            if (!block.OnBlockInteractStart(world, player, selection))
                throw new Exception("Shift + right-click did not handle the loose stone.");
            write.Invoke(null, [pit, progress]);
            if (slot.StackSize != 16 - i || progress.GetInt("vsslssmodStoneRingStones") != i)
                throw new Exception("Native stone interaction did not consume and save exactly one stone.");
        }
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 4) throw new Exception("A completed stone ring consumed more stones.");

        read.Invoke(null, [pit, new TreeAttribute()]);
        side = EnumAppSide.Client;
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 4) throw new Exception("Client prediction consumed a stone.");
        side = EnumAppSide.Server;
        allowed = false;
        if (block.OnBlockInteractStart(world, player, selection) || slot.StackSize != 4)
            throw new Exception("A denied land claim accepted stones.");
        allowed = true;
        typeof(BlockEntityFirepit).GetField("fuelBurnTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(pit, 10f);
        block.OnBlockInteractStart(world, player, selection);
        if (slot.StackSize != 4) throw new Exception("A burning firepit accepted stones.");

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
        Console.WriteLine("PASS native Shift + right-click stone insertion, 12-stone cap, client/server ownership, claims, burning rejection and fuel passthrough");
    }
}
