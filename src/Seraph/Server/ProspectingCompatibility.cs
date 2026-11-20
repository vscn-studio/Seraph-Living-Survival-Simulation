using HarmonyLib;
using Seraph.Network;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.Server;

/// <summary>
/// Appends the virtual aquifer reading to the vanilla prospecting result.
/// The vanilla sampler, tool durability and block interaction remain the owner
/// of prospecting; this patch only emits an additional server chat message.
/// </summary>
internal sealed class ProspectingCompatibility : IDisposable
{
    private const string HarmonyId = "vsslssmod.prospecting";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ServerCoordinator owner;
    private bool patched;
    private static ProspectingCompatibility? active;
    private static readonly Dictionary<string, (long At, int X, int Y, int Z)> LastResults = [];

    internal ProspectingCompatibility(ServerCoordinator owner)
    {
        this.owner = owner;
        try
        {
            var pickType = AccessTools.TypeByName("Vintagestory.GameContent.ItemProspectingPick");
            var method = pickType is null
                ? null
                : AccessTools.Method(pickType, "PrintProbeResults", [
                    typeof(IWorldAccessor), typeof(IServerPlayer), typeof(ItemSlot), typeof(BlockPos)])
                  ?? pickType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                      .FirstOrDefault(candidate => candidate.Name == "PrintProbeResults"
                          && candidate.GetParameters().Length == 4);
            if (method is null)
            {
                owner.LogProspectingWarning("原版探矿结果方法未找到，地下水聊天追加已停用。 / Vanilla prospecting result method was not found; groundwater chat append is disabled.");
                return;
            }

            active = this;
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(ProspectingCompatibility), nameof(AfterProbe)));
            patched = true;
            owner.LogProspectingInfo($"地下水探矿聊天联动已加载：{method.DeclaringType?.FullName}.{method.Name}");
        }
        catch (Exception ex)
        {
            owner.LogProspectingWarning($"地下水探矿联动加载失败，已保持原版探矿：{ex.Message}");
            harmony.UnpatchAll(HarmonyId);
        }
    }

    private static void AfterProbe(IWorldAccessor world, IServerPlayer splr, ItemSlot itemslot, BlockPos pos)
    {
        var bridge = active;
        if (bridge is null || !bridge.patched || world.Side != EnumAppSide.Server) return;
        try
        {
            var now = world.ElapsedMilliseconds;
            lock (LastResults)
            {
                if (LastResults.TryGetValue(splr.PlayerUID, out var previous)
                    && now - previous.At < 750
                    && previous.X == pos.X && previous.Y == pos.Y && previous.Z == pos.Z) return;
                LastResults[splr.PlayerUID] = (now, pos.X, pos.Y, pos.Z);
                if (LastResults.Count > 256)
                    foreach (var stale in LastResults.Where(p => now - p.Value.At > 10000).Select(p => p.Key).ToArray())
                        LastResults.Remove(stale);
            }
            bridge.owner.AppendGroundwaterProbe(splr, pos);
        }
        catch (Exception ex)
        {
            bridge.owner.LogProspectingWarning($"地下水探矿读数已跳过，不影响原版探矿：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (patched) harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(active, this)) active = null;
        lock (LastResults) LastResults.Clear();
    }
}
