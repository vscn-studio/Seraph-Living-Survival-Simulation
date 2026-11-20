using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Retains native application time, interruptions, sounds and actual item debit.</summary>
internal sealed class NativeBandageBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.bandages");
    [ThreadStatic] private static Entity? healingTarget;
    private sealed record Use(Entity Target, ItemSlot Slot, CollectibleObject Item, int Count, long Wound,
        bool Antiseptic, bool Poultice, double Strength, Entity? Previous, Entity Healer)
    {
        internal ItemStack Original { get; } = Slot.Itemstack!.Clone();
        internal bool Completed { get; set; }
        internal bool Committed { get; set; }
    }
    private static readonly System.Reflection.MethodInfo Target = AccessTools.Method(typeof(CollectibleBehaviorHealingItem), "GetTargetEntity");
    private static readonly System.Reflection.MethodInfo Time = AccessTools.Method(typeof(CollectibleBehaviorHealingItem), "GetApplicationTime");

    internal NativeBandageBridge()
    {
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleBehaviorHealingItem), "OnHeldInteractStart"),
            postfix: new HarmonyMethod(typeof(NativeBandageBridge), nameof(Started)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleBehaviorHealingItem), "OnHeldInteractStop"),
        prefix: new HarmonyMethod(typeof(NativeBandageBridge), nameof(Begin)),
        postfix: new HarmonyMethod(typeof(NativeBandageBridge), nameof(Complete)),
        finalizer: new HarmonyMethod(typeof(NativeBandageBridge), nameof(Finish)));
    }

    private static void Started(EntityAgent byEntity, ref EnumHandHandling handHandling)
    {
        if (byEntity.World.Side == EnumAppSide.Server && handHandling != EnumHandHandling.NotHandled)
            Owner(byEntity)?.BeginNativeCare(byEntity);
    }

    internal static bool SuppressesHealing(Entity entity) => ReferenceEquals(entity, healingTarget);
    private static ServerCoordinator? Owner(Entity entity) => entity.World.Side == EnumAppSide.Server
        ? entity.Api.ModLoader.GetModSystem<SeraphModSystem>().Server : null;
    private static void Begin(CollectibleBehaviorHealingItem __instance, float secondsUsed, ItemSlot slot,
        EntityAgent byEntity, EntitySelection entitySel, out Use? __state)
    {
        __state = null;
        if (byEntity.World.Side != EnumAppSide.Server || slot.Itemstack?.Collectible is not { Code: not null } item
            || !float.IsFinite(secondsUsed) || secondsUsed < (float)Time.Invoke(__instance, [byEntity])!) return;
        if (Target.Invoke(__instance, [slot, byEntity, entitySel]) is not Entity { Alive: true } target
            || Owner(target) is not { } owner) return;
        var poultice = item.Code.Path.StartsWith("poultice-", StringComparison.Ordinal);
        var supportedCare = item.Code.Domain == "game" && item.Code.Path is ("bandage-clean" or "bandage-alcoholed"
            or "poultice-reed-horsetail" or "poultice-linen-horsetail" or "poultice-reed-honey-sulfur" or "poultice-linen-honey-sulfur");
        var wound = supportedCare ? poultice ? owner.SelectPoulticeWound(target) : owner.SelectBandageWound(target) : 0;
        if (wound <= 0 && !owner.IsOwnedDowned(target)) return;
        __state = new(target, slot, item, slot.StackSize, wound,
            item.Code.Path == "bandage-alcoholed" || item.Code.Path.EndsWith("honey-sulfur", StringComparison.Ordinal),
            poultice, __instance.Health, healingTarget, byEntity);
        healingTarget = target;
    }
    private static void Complete(Use? __state)
    {
        if (__state is not { Completed: false } use) return;
        var current = use.Slot.Itemstack;
        var consumedExactlyOne = current is null
            ? use.Count == 1
            : current.StackSize == use.Count - 1
                && current.Collectible is { } collectible
                && collectible.Id == use.Item.Id
                && collectible.Code?.Equals(use.Item.Code) == true;
        if (!consumedExactlyOne) return;
        use.Completed = true;
        var owner = Owner(use.Target);
        if (owner is null) { Restore(use); return; }
        var applied = use.Wound <= 0 && owner.IsOwnedDowned(use.Target) && owner.CommitConsumedRescue(use.Target);
        if (use.Wound > 0) applied = use.Poultice
            ? owner.ApplyConsumedPoultice(use.Target, use.Wound, use.Strength, use.Antiseptic)
            : owner.ApplyConsumedBandage(use.Target, use.Wound, use.Antiseptic);
        use.Committed = applied;
        if (applied) NativeDownedBridge.RescueAfterConsumedTreatment(use.Target, use.Healer);
        else Restore(use);
    }
    private static Exception? Finish(Exception? __exception, Use? __state)
    {
        if (__state is { } use)
        {
            healingTarget = use.Previous;
            if (__exception is not null && !use.Committed) Restore(use);
        }
        return __exception;
    }
    private static void Restore(Use use)
    { use.Slot.Itemstack = use.Original.Clone(); use.Slot.MarkDirty(); }
    public void Dispose() => harmony.UnpatchAll("vsslssmod.bandages");
}
