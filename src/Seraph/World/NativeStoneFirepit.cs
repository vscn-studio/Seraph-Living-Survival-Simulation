using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Installs a stone surround while retaining the native firepit entity and inventory.</summary>
internal sealed class NativeStoneFirepit : IDisposable
{
    private const string PatchId = "vsslssmod.stonefirepit";
    private const string StonesKey = "vsslssmodStoneRingStones";
    private const string MaterialsKey = "vsslssmodStoneRingMaterials";
    private const int RequiredStones = 12;
    private static readonly ConditionalWeakTable<BlockEntityFirepit, UpgradeState> States = new();
    private static readonly ConditionalWeakTable<IWorldAccessor, ItemStack[]> StoneExamples = new();
    private static Harmony? harmony;
    private static int owners;

    private sealed class UpgradeState
    {
        internal int Stones;
        internal readonly Dictionary<string, int> Materials = new();
        internal bool Installed => Stones >= RequiredStones;
    }

    internal NativeStoneFirepit()
    {
        if (owners++ != 0) return;
        harmony = new(PatchId);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(BlockFirepit), "OnBlockInteractStart"),
                prefix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(Interact)));
            harmony.Patch(AccessTools.Method(typeof(BlockFirepit), "GetPlacedBlockInteractionHelp"),
                postfix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(AddInteractionHelp)));
            harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), "get_BurnDurationModifier"),
                postfix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(BurnDuration)));
            harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.ToTreeAttributes)),
                postfix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(WriteState)));
            harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.FromTreeAttributes)),
                postfix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(ReadState)));
            harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.getOrCreateMesh)),
                prefix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(StoneMesh)));
            harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.GetDrops),
                [typeof(IWorldAccessor), typeof(BlockPos), typeof(IPlayer), typeof(float)]),
                postfix: new HarmonyMethod(typeof(NativeStoneFirepit), nameof(StoneDrops)));
        }
        catch
        {
            harmony.UnpatchAll(PatchId); harmony = null; owners = 0; throw;
        }
    }

    private static bool IsStoneBlock(Block? block) => block?.Code?.Domain == "vsslssmod" && block.FirstCodePart() == "stonefirepit";
    private static bool Installed(BlockEntityFirepit pit) => IsStoneBlock(pit.Block)
        || States.TryGetValue(pit, out var state) && state.Installed;

    private static UpgradeState State(BlockEntityFirepit pit) => States.GetValue(pit, p =>
    {
        var state = new UpgradeState { Stones = IsStoneBlock(p.Block) ? RequiredStones : 0 };
        if (state.Stones > 0) state.Materials["game:stone-granite"] = state.Stones;
        return state;
    });

    private static bool Interact(BlockFirepit __instance, IWorldAccessor world, IPlayer byPlayer,
        BlockSelection blockSel, ref bool __result)
    {
        if (world is null || byPlayer is null || blockSel is null
            || !byPlayer.Entity.Controls.ShiftKey
            || byPlayer.InventoryManager.ActiveHotbarSlot?.Itemstack is not { } held
            || !IsLooseStone(held)) return true;

        if (__instance.Stage != 5) return true;
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
        {
            __result = false;
            return false;
        }
        var entity = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityFirepit;
        if (entity is null) return true;
        __result = true;
        var state = State(entity);
        if (Installed(entity)) return false;
        if (entity.IsBurning)
        {
            Notify(world, byPlayer, "vsslssmod:firepit-need-unlit");
            return false;
        }
        var required = RequiredStones - state.Stones;
        if (held.StackSize < required)
        {
            Notify(world, byPlayer, "vsslssmod:firepit-need-stones", required);
            return false;
        }
        var target = world.GetBlock(new AssetLocation("vsslssmod", "stonefirepit-" + __instance.LastCodePart()));
        if (target is null) return false;
        if (world.Side == EnumAppSide.Client)
        {
            if (byPlayer is IClientPlayer client) client.TriggerFpAnimation((EnumHandInteract)2);
            return false;
        }

        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (slot?.Itemstack is not { } stack || !IsLooseStone(stack) || stack.StackSize < required) return false;
        // ExchangeBlock keeps the native entity, fuel, contents and temperature intact.
        // One batch produces one slot synchronization and one terrain rebuild.
        var material = stack.Collectible.Code.ToString();
        if (byPlayer.WorldData?.CurrentGameMode != EnumGameMode.Creative) slot.TakeOut(required);
        slot.MarkDirty();
        state.Materials[material] = state.Materials.GetValueOrDefault(material) + required;
        state.Stones = RequiredStones;
        world.BlockAccessor.ExchangeBlock(target.Id, blockSel.Position);
        entity.Block = target;
        entity.MarkDirty(true);
        Notify(world, byPlayer, "vsslssmod:firepit-stone-ring-complete");
        return false;
    }

    private static bool IsLooseStone(ItemStack stack) => stack.Class == EnumItemClass.Item && stack.Collectible.Code is { } code
        && (string.Equals(stack.Collectible.FirstCodePart(), "stone", StringComparison.OrdinalIgnoreCase)
            || code.Path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase))
        && stack.StackSize > 0;

    private static void AddInteractionHelp(BlockFirepit __instance, IWorldAccessor world,
        BlockSelection selection, IPlayer forPlayer, ref WorldInteraction[] __result)
    {
        if (__instance.Stage != 5 || selection is null
            || world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityFirepit entity
            || entity.IsBurning || Installed(entity)) return;
        var examples = StoneExamples.GetValue(world, w => w.Items.Where(i => i.Code is not null
                && (i.FirstCodePart() == "stone" || i.Code.Path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase)))
            .Take(1).Select(i => new ItemStack(i, RequiredStones)).ToArray());
        if (examples.Length == 0) return;
        var interaction = new WorldInteraction
        {
            ActionLangCode = "vsslssmod:help-firepit-stone-ring",
            MouseButton = EnumMouseButton.Right,
            HotKeyCode = "shift",
            Itemstacks = examples
        };
        __result = [.. (__result ?? []), interaction];
    }

    private static void Notify(IWorldAccessor world, IPlayer player, string key, params object[] args)
    {
        if (world.Api.Side == EnumAppSide.Server && player is IServerPlayer serverPlayer)
            serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, key, args), (EnumChatType)4, null);
    }

    private static void BurnDuration(BlockEntityFirepit __instance, ref float __result)
    {
        if (Installed(__instance)) __result *= 1.3f;
    }

    private static void WriteState(BlockEntityFirepit __instance, ITreeAttribute tree)
    {
        var state = State(__instance);
        tree.SetInt(StonesKey, state.Stones);
        var materials = new TreeAttribute();
        foreach (var (code, count) in state.Materials) materials.SetInt(code, count);
        tree[MaterialsKey] = materials;
    }

    private static void ReadState(BlockEntityFirepit __instance, ITreeAttribute tree)
    {
        var state = State(__instance);
        state.Stones = Math.Clamp(tree.GetInt(StonesKey, IsStoneBlock(__instance.Block) ? RequiredStones : 0), 0, RequiredStones);
        state.Materials.Clear();
        var remaining = state.Stones;
        if (tree.GetTreeAttribute(MaterialsKey) is { } materials)
            foreach (var entry in materials)
            {
                var count = Math.Clamp(materials.GetInt(entry.Key), 0, remaining);
                if (count > 0) state.Materials[entry.Key] = count;
                remaining -= count;
            }
        // Older saves stored only a count, so retain that value as granite.
        if (remaining > 0) state.Materials["game:stone-granite"] = state.Materials.GetValueOrDefault("game:stone-granite") + remaining;
    }

    private static void StoneDrops(Block __instance, IWorldAccessor world, BlockPos pos, ref ItemStack[] __result)
    {
        if (__instance is not BlockFirepit || world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityFirepit pit) return;
        var state = State(pit);
        if (state.Stones == 0) return;
        var drops = new List<ItemStack>(__result ?? []);
        foreach (var (code, count) in state.Materials)
        {
            var stone = world.GetItem(new AssetLocation(code)) ?? world.GetItem(new AssetLocation("game:stone-granite"));
            if (stone is not null) drops.Add(new ItemStack(stone, count));
        }
        // The native container still owns fuel, cooking vessels, food and ash drops.
        __result = drops.ToArray();
    }

    private static bool StoneMesh(BlockEntityFirepit __instance, string burnstate, string contentstate, ref MeshData __result)
    {
        if (!Installed(__instance) || __instance.Api is not ICoreClientAPI capi) return true;
        var block = capi.World.GetBlock(new AssetLocation("vsslssmod", "stonefirepit-" + burnstate));
        if (block is null) return true;
        var meshes = ObjectCacheUtil.GetOrCreate(capi, "vsslssmod-stonefirepit-meshes", () => new Dictionary<string, MeshData>());
        lock (meshes)
        {
            var key = burnstate + "-" + contentstate;
            if (!meshes.TryGetValue(key, out var mesh))
            {
                // Keep native flames, pot supports, spit geometry and content rendering.
                var fire = Shape.TryGet(capi, "game:shapes/block/wood/firepit/" + key + ".json");
                var ring = Shape.TryGet(capi, "vsslssmod:shapes/block/stonefirepit-ring.json");
                if (fire is null || ring is null) return true;
                capi.Tesselator.TesselateShape(block, fire, out mesh);
                capi.Tesselator.TesselateShape(block, ring, out var surround);
                mesh.AddMeshData(surround);
                meshes[key] = mesh;
            }
            __result = mesh;
        }
        return false;
    }

    public void Dispose()
    {
        if (--owners == 0) { harmony?.UnpatchAll(PatchId); harmony = null; }
    }
}
