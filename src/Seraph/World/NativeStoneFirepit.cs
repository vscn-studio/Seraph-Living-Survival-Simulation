using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Adds the BP stone-ring firepit upgrade to the native unlit firepit.</summary>
internal sealed class NativeStoneFirepit : IDisposable
{
    private const string PatchId = "vsslssmod.stonefirepit";
    private const string StonesKey = "vsslssmodStoneRingStones";
    private const int RequiredStones = 12;
    private static readonly ConditionalWeakTable<BlockEntityFirepit, UpgradeState> States = new();
    private static Harmony? harmony;
    private static int owners;

    private sealed class UpgradeState { internal int Stones; internal bool Installed => Stones >= RequiredStones; }

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
        }
        catch
        {
            harmony.UnpatchAll(PatchId); harmony = null; owners = 0; throw;
        }
    }

    private static bool Installed(BlockEntityFirepit pit) => States.TryGetValue(pit, out var state) && state.Installed;

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
        var state = States.GetValue(entity, _ => new UpgradeState());
        if (state.Installed)
        {
            Notify(world, byPlayer, "vsslssmod:firepit-stone-ring-complete");
            return false;
        }
        if (entity.IsBurning)
        {
            Notify(world, byPlayer, "vsslssmod:firepit-need-unlit");
            return false;
        }
        if (world.Side == EnumAppSide.Client)
        {
            if (byPlayer is IClientPlayer client) client.TriggerFpAnimation((EnumHandInteract)2);
            return false;
        }

        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (slot?.Itemstack is not { } stack || !IsLooseStone(stack)) return false;
        // The item is charged only after the server has checked the unlit state and access.
        slot.TakeOut(1);
        slot.MarkDirty();
        state.Stones = Math.Min(RequiredStones, state.Stones + 1);
        entity.MarkDirty(true);
        if (state.Installed) Notify(world, byPlayer, "vsslssmod:firepit-stone-ring-complete");
        else Notify(world, byPlayer, "vsslssmod:firepit-stone-added", state.Stones);
        return false;
    }

    private static bool IsLooseStone(ItemStack stack) => stack.Collectible.Code is { } code
        && (string.Equals(stack.Collectible.FirstCodePart(), "stone", StringComparison.OrdinalIgnoreCase)
            || code.Path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase))
        && stack.StackSize > 0;

    private static void AddInteractionHelp(BlockFirepit __instance, IWorldAccessor world,
        BlockSelection selection, IPlayer forPlayer, ref WorldInteraction[] __result)
    {
        if (__instance.Stage != 5 || selection is null
            || world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityFirepit entity
            || entity.IsBurning || Installed(entity)) return;
        var examples = world.Items.Where(i => i.Code is not null
                && (i.FirstCodePart() == "stone" || i.Code.Path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase)))
            .Take(1).Select(i => new ItemStack(i)).ToArray();
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
        => tree.SetInt(StonesKey, States.TryGetValue(__instance, out var state) ? Math.Clamp(state.Stones, 0, RequiredStones) : 0);

    private static void ReadState(BlockEntityFirepit __instance, ITreeAttribute tree)
        => States.GetValue(__instance, _ => new UpgradeState()).Stones = Math.Clamp(tree.GetInt(StonesKey), 0, RequiredStones);

    public void Dispose()
    {
        if (--owners == 0) { harmony?.UnpatchAll(PatchId); harmony = null; }
    }
}
