using HarmonyLib;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seraph.Server;

/// <summary>Only metadata belonging to a held melee cycle is tagged. Native frame callbacks
/// (including the real hit and cycle completion) advance together; shared assets stay untouched.</summary>
internal static class AdrenalineAttackTiming
{
    private static readonly ConditionalWeakTable<AnimationMetaData, EntityPlayer> Owners = new();

    internal static void Tag(AnimationManager? manager, EntityPlayer owner, string code)
    {
        if (manager is null) return;
        TagCode(manager, owner, code);
        TagCode(manager, owner, code + "-fp");
        TagCode(manager, owner, code + "-ifp");
    }

    private static void TagCode(AnimationManager manager, EntityPlayer owner, string code)
    {
        if (manager.ActiveAnimationsByAnimCode is not { } animations) return;
        // Meta codes and animation codes need not be identical.
        AnimationMetaData? source = null;
        string? key = null;
        foreach (var entry in animations)
            if (entry.Value.Code == code || entry.Key == code) { source = entry.Value; key = entry.Key; break; }
        if (source is null || key is null || Owners.TryGetValue(source, out _)) return;
        var instance = source.Clone();
        Owners.Add(instance, owner);
        animations[key] = instance;
        if (manager.Animator?.GetAnimationState(instance.Animation) is { } running) running.meta = instance;
    }

    internal static void Progress(RunningAnimation __instance, ref float dt)
    {
        if (__instance.meta is { } meta && Owners.TryGetValue(meta, out var owner)
            && NativeInjuryActionBridge.Functions(owner) is { Manipulation: true, CanUseActiveHand: true } f)
            dt *= (float)Math.Clamp(f.MeleeAttackSpeed, 1, 1.2);
    }
}
