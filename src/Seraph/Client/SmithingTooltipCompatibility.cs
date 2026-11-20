using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace Seraph.Client;

/// <summary>
/// SmithingPlus 1.10.7 evaluates its repair selector while the inventory search
/// cache enumerates items on a worker thread. Native regex timeouts escape that
/// thread and terminate the client. Replace only this tooltip's predicate; tool
/// repair recipes, eligibility and all other wildcard users keep their own code.
/// </summary>
internal sealed class SmithingTooltipCompatibility : IDisposable
{
    internal const string HarmonyId = "vsslssmod.smithing-tooltip-compat";
    private readonly Harmony harmony = new(HarmonyId);
    private static readonly ConcurrentDictionary<string, Lazy<Regex?>> Patterns = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);
    private static Action<string>? warning;

    internal SmithingTooltipCompatibility(ICoreClientAPI api)
    {
        var type = AccessTools.TypeByName("SmithingPlus.ToolRecovery.CollectibleBehaviorRepairableTool");
        if (type is null) return;
        var method = AccessTools.DeclaredMethod(type, "GetHeldItemInfo",
            [typeof(ItemSlot), typeof(StringBuilder), typeof(IWorldAccessor), typeof(bool)]);
        if (method is null)
        {
            api.Logger.Warning("[vsslssmod] SmithingPlus tooltip compatibility target unavailable.");
            return;
        }
        warning = message => api.Logger.Warning("[vsslssmod] {0}", message);
        harmony.Patch(method, transpiler: new HarmonyMethod(typeof(SmithingTooltipCompatibility), nameof(ReplaceMatcher)));
        api.Logger.Notification("[vsslssmod] SmithingPlus inventory tooltip selector uses non-backtracking matching.");
    }

    private static IEnumerable<CodeInstruction> ReplaceMatcher(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(WildcardUtil), nameof(WildcardUtil.Match), [typeof(string), typeof(string)]);
        var replacement = AccessTools.Method(typeof(SmithingTooltipCompatibility), nameof(Match));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native)) { instruction.operand = replacement; replaced++; }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unexpected SmithingPlus tooltip selector call count: " + replaced);
    }

    internal static bool Match(string selector, string code)
    {
        if (string.IsNullOrEmpty(selector) || selector[0] != '@') return WildcardUtil.Match(selector, code);
        try
        {
            var regex = Patterns.GetOrAdd(selector, key => new Lazy<Regex?>(() => Compile(key))).Value;
            // Preserve advanced user regex features (lookarounds/backreferences)
            // that the non-backtracking engine cannot represent.
            return regex?.IsMatch(code) ?? WildcardUtil.Match(selector, code);
        }
        catch (Exception error) when (error is RegexMatchTimeoutException or ArgumentException)
        {
            if (Reported.TryAdd(selector, 0))
                warning?.Invoke("SmithingPlus tooltip selector failed (" + error.GetType().Name
                    + "); repair history omitted for this item. Check RepairableToolSelector in SmithingPlus.json.");
            return false;
        }
    }

    private static Regex? Compile(string selector)
    {
        try
        {
            // Same anchors/case/culture as WildcardUtil. This engine is linear in
            // input length, so it needs no wall-clock timeout during world loading.
            return new Regex("^" + selector[1..] + "$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                Regex.InfiniteMatchTimeout);
        }
        catch (NotSupportedException) { return null; }
    }

    public void Dispose()
    {
        harmony.UnpatchAll(HarmonyId);
        warning = null;
        Patterns.Clear(); Reported.Clear();
    }
}
