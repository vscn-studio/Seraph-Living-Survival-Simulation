using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Appends an output-only slot after the seven native slots. Native inventory persistence,
/// synchronization, transactions and block-break drops own it, just like the original output.</summary>
internal sealed class NativeAshGeneration : IDisposable
{
    private const string PatchId = "vsslssmod.ashgeneration";
    internal const int AshSlot = 7;
    private static int owners;
    private static Harmony? harmony;
    private static readonly FieldInfo Slots = AccessTools.Field(typeof(InventorySmelting), "slots");
    private static readonly MethodInfo InteractionRange = AccessTools.Method(typeof(IPlayer), "IsInInteractionRangeOf", [typeof(Vintagestory.API.MathTools.BlockPos), typeof(float)]);
    private static readonly FieldInfo FuelTime = AccessTools.Field(typeof(BlockEntityFirepit), "fuelBurnTime");

    internal NativeAshGeneration()
    {
        if (owners++ != 0) return;
        harmony = new Harmony(PatchId);
        try
        {
        foreach (var ctor in typeof(InventorySmelting).GetConstructors())
            harmony.Patch(ctor, postfix: new(typeof(NativeAshGeneration), nameof(EnsureSlot)));
        harmony.Patch(AccessTools.Method(typeof(InventorySmelting), nameof(InventorySmelting.FromTreeAttributes)),
            postfix: new(typeof(NativeAshGeneration), nameof(EnsureSlot)));
        harmony.Patch(AccessTools.Method(typeof(InventorySmelting), "NewSlot"),
            postfix: new(typeof(NativeAshGeneration), nameof(NewSlot)));
        harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), "OnBurnTick"),
            transpiler: new(typeof(NativeAshGeneration), nameof(BurnTick)));
        harmony.Patch(AccessTools.Method(typeof(GuiDialogBlockEntityFirepit), "SetupDialog"),
            transpiler: new(typeof(NativeAshGeneration), nameof(Dialog)));
        harmony.Patch(AccessTools.Method(typeof(BlockEntityFirepit), nameof(BlockEntityFirepit.OnReceivedClientPacket)),
            prefix: new(typeof(NativeAshGeneration), nameof(Authorize)));
        }
        catch { harmony.UnpatchAll(PatchId); harmony = null; owners = 0; throw; }
    }

    private static void EnsureSlot(InventorySmelting __instance)
    {
        var slots = (ItemSlot[])Slots.GetValue(__instance)!;
        if (slots.Length == AshSlot) Slots.SetValue(__instance, slots.Concat([new ItemSlotOutput(__instance)]).ToArray());
        else if (slots.Length != AshSlot + 1) throw new InvalidOperationException("Unexpected native smelting inventory size.");
        __instance[AshSlot].MaxSlotStackSize = 64;
    }

    private static void NewSlot(InventorySmelting __instance, int i, ref ItemSlot __result)
    { if (i == AshSlot) __result = new ItemSlotOutput(__instance) { MaxSlotStackSize = 64 }; }

    private static IEnumerable<CodeInstruction> BurnTick(IEnumerable<CodeInstruction> instructions)
    {
        var stores = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            // Observe the decrement before the native tick can ignite another unit.
            // The later store only clamps the elapsed timer to zero.
            if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, FuelTime) && ++stores == 1)
            {
                yield return new(OpCodes.Ldarg_0);
                yield return new(OpCodes.Call, AccessTools.Method(typeof(NativeAshGeneration), nameof(AfterFuelDecrement)));
            }
        }
        if (stores != 2) throw new InvalidOperationException("Native firepit burn accounting changed; expected decrement and clamp.");
    }

    private static void AfterFuelDecrement(BlockEntityFirepit firepit)
    {
        if (firepit.Api?.Side != EnumAppSide.Server || (float)FuelTime.GetValue(firepit)! > 0) return;
        var slot = firepit.Inventory[AshSlot];
        if (slot.StackSize >= 64) return;
        var ash = firepit.Api.World.GetItem(new AssetLocation("vsslssmod:ash"));
        if (ash is null || !slot.Empty && !ReferenceEquals(slot.Itemstack.Collectible, ash)) return;
        if (slot.Empty) slot.Itemstack = new ItemStack(ash);
        else slot.Itemstack.StackSize++;
        slot.MarkDirty();
        firepit.MarkDirty();
    }

    private static IEnumerable<CodeInstruction> Dialog(IEnumerable<CodeInstruction> instructions)
    {
        var count = 0;
        foreach (var instruction in instructions)
        {
            // Insert inside the existing background's child group.
            if (instruction.operand is MethodInfo { Name: "EndChildElements", DeclaringType: { } type } && type == typeof(GuiComposer))
            {
                count++;
                yield return new(OpCodes.Ldarg_0);
                yield return new(OpCodes.Call, AccessTools.Method(typeof(NativeAshGeneration), nameof(AddSlot)));
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Native firepit composition changed.");
    }

    private static GuiComposer AddSlot(GuiComposer composer, GuiDialogBlockEntityFirepit dialog)
    {
        
        var fuel = composer.GetSlotGrid("fuelslot").Bounds;
        var bounds = fuel.RightCopy(153 - fuel.fixedWidth, 0);
        return composer.AddStaticText(LocalizedText.Get("vsslssmod:firepit-ash"), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(bounds.fixedX, bounds.fixedY - 23, 70, 23))
            .AddItemSlotGrid(dialog.Inventory, AccessTools.Method(typeof(GuiDialogBlockEntityFirepit), "SendInvPacket").CreateDelegate<Action<object>>(dialog), 1, [AshSlot], bounds, "vsslssmod-ash");
    }

    private static bool Authorize(BlockEntityFirepit __instance, IPlayer player, int packetid)
    {
        if (__instance.Api?.Side != EnumAppSide.Server || packetid >= 1000) return true;
        return player.Entity.Alive && (bool)InteractionRange.Invoke(player, [__instance.Pos, .5f])!
            && __instance.Inventory.openedByPlayerGUIds.Contains(player.PlayerUID)
            && __instance.Api.World.Claims.TryAccess(player, __instance.Pos, EnumBlockAccessFlags.Use);
    }

    public void Dispose()
    {
        if (--owners == 0) { harmony?.UnpatchAll(PatchId); harmony = null; }
    }
}
