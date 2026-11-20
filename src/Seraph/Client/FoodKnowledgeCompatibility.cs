using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seraph.Client;

/// <summary>Optional bridge to Foragers Gamble's per-player food discovery state.</summary>
internal static class FoodKnowledgeCompatibility
{
    private static readonly Lazy<MethodInfo?> IsKnownMethod = new(() =>
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("ForagersGamble.Knowledge", throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        return type?.GetMethod("IsKnown", BindingFlags.Public | BindingFlags.Static, null,
            [typeof(EntityAgent), typeof(ItemStack)], null);
    });

    internal static bool IsKnownToPlayer(EntityPlayer player, ItemStack stack)
    {
        var survival = player.Player?.WorldData?.CurrentGameMode == EnumGameMode.Survival;
        var method = IsKnownMethod.Value;
        bool? known = null;
        if (method is not null)
        {
            try { known = method.Invoke(null, [player, stack]) is true; }
            catch { }
        }
        return ShouldList(survival, known);
    }

    internal static bool ShouldList(bool survival, bool? known) => !survival || known is not false;
}
