using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Scale native fertilizer nutrient inputs, without mutating shared collectible attributes.</summary>
internal sealed class NativeManureFertilizer : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.manuremass");
    internal NativeManureFertilizer() => harmony.Patch(
        AccessTools.DeclaredMethod(typeof(BlockEntitySoilNutrition), nameof(BlockEntitySoilNutrition.OnBlockInteract)),
        transpiler: new(typeof(NativeManureFertilizer), nameof(Inputs)));
    private static IEnumerable<CodeInstruction> Inputs(IEnumerable<CodeInstruction> instructions)
    {
        var count = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field
                && field.DeclaringType?.Name == "FertilizerProps" && field.Name is "N" or "P" or "K")
            {
                yield return new(OpCodes.Ldarg_1);
                yield return new(OpCodes.Call, AccessTools.Method(typeof(NativeManureFertilizer), nameof(Scale)));
                count++;
            }
        }
        if (count != 3) throw new InvalidOperationException("Native fertilizer inputs do not match VS 1.22.7.");
    }
    private static float Scale(float nutrient, IPlayer player)
    {
        var stack = player.InventoryManager.ActiveHotbarSlot.Itemstack;
        if (stack?.Collectible.Code is { Domain: "vsslssmod", Path: "ash" }) return nutrient * .25f;
        return LatrineWasteStorage.IsManure(stack) ? nutrient * (float)(ManureMass.PerItem(stack!) / 100) : nutrient;
    }
    public void Dispose() => harmony.UnpatchAll("vsslssmod.manuremass");
}
