using System.Reflection;
using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Seraph.World;

namespace Seraph.Server;

/// <summary>Observe accepted native work, never UI opens or automated production.</summary>
internal sealed class NativeLaborBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.labor");
    internal NativeLaborBridge()
    {
        foreach (var type in new[] { typeof(ItemFirestarter), typeof(BlockPan) })
        {
            harmony.Patch(AccessTools.DeclaredMethod(type, "OnHeldInteractStep"),
                prefix: new(typeof(NativeLaborBridge), nameof(HeldGate)),
                postfix: new(typeof(NativeLaborBridge), nameof(HeldStep)));
            foreach (var end in new[] { "OnHeldInteractStop", "OnHeldInteractCancel" })
                harmony.Patch(AccessTools.DeclaredMethod(type, end), prefix: new(typeof(NativeLaborBridge), nameof(HeldEnd)));
        }
        foreach (var type in new[] { typeof(BlockEntityAnvil), typeof(BlockEntityClayForm), typeof(BlockEntityKnappingSurface) })
        {
            var target = AccessTools.GetDeclaredMethods(type).Single(m => m.Name == "OnUseOver"
                && m.GetParameters().Any(p => p.ParameterType.Name == "Vec3i"));
            harmony.Patch(target, prefix: new(typeof(NativeLaborBridge), nameof(VoxelStart)),
                postfix: new(typeof(NativeLaborBridge), nameof(VoxelEnd)));
        }
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityChisel), "UpdateVoxel"),
            prefix: new(typeof(NativeLaborBridge), nameof(ChiselStart)),
            finalizer: new(typeof(NativeLaborBridge), nameof(ChiselFinish)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityChisel), "SetVoxel",
                [typeof(Vintagestory.API.MathTools.Vec3i), typeof(bool), typeof(IPlayer), typeof(byte)]),
            prefix: new(typeof(NativeLaborBridge), nameof(ChiselGate)), postfix: new(typeof(NativeLaborBridge), nameof(Chiseled)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityQuern), "SetPlayerGrinding"),
            prefix: new(typeof(NativeLaborBridge), nameof(GrindGate)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityQuern), "Every100ms"),
            prefix: new(typeof(NativeLaborBridge), nameof(GrindTick)));
        var outputType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Common.ItemSlotCraftingOutput", true)!;
        var process = typeof(CollectibleBehaviorGroundStoredProcessable);
        harmony.Patch(AccessTools.DeclaredMethod(process, "OnContainedInteractStep"),
            prefix: new(typeof(NativeLaborBridge), nameof(ContainedGate)), postfix: new(typeof(NativeLaborBridge), nameof(ContainedStep)));
        harmony.Patch(AccessTools.DeclaredMethod(process, "OnContainedInteractStop"),
            prefix: new(typeof(NativeLaborBridge), nameof(ContainedStopGate)), postfix: new(typeof(NativeLaborBridge), nameof(ContainedStopped)));
        harmony.Patch(AccessTools.DeclaredMethod(process, "OnContainedInteractCancel"),
            postfix: new(typeof(NativeLaborBridge), nameof(ContainedEnd)));
        harmony.Patch(AccessTools.DeclaredMethod(outputType, "TryPutInto"),
            prefix: new(typeof(NativeLaborBridge), nameof(CraftGate)));
        // Shift-crafting loops here; recheck before each transfer so exhaustion
        // stops the next transaction. FlipWith also enters this path directly.
        harmony.Patch(AccessTools.DeclaredMethod(outputType, "TryPutIntoNoEvent"),
            prefix: new(typeof(NativeLaborBridge), nameof(CraftGate)));
        var gridType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Common.InventoryCraftingGrid", true)!;
        var consume = AccessTools.DeclaredMethod(gridType, "ConsumeIngredients");
        if (consume.ReturnType == typeof(void))
            harmony.Patch(consume, prefix: new(typeof(NativeLaborBridge), nameof(CraftingStarted)),
                postfix: new(typeof(NativeLaborBridge), nameof(CraftingFinished)));
        else
            harmony.Patch(consume, postfix: new(typeof(NativeLaborBridge), nameof(Crafted)));

        // Block placement is a discrete hand action.  Gate the native mutation
        // before the block accessor is changed; the successful placement event
        // records the charge exactly once for the server transaction.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Block), "DoPlaceBlock"),
            prefix: new(typeof(NativeLaborBridge), nameof(PlaceGate)));

        // RKN replaces the inventory grid with its own in-world surface.  Keep
        // this optional so the base game and servers without RKN do not pay a
        // reflection or patching cost.  Its accepted crafting step is the same
        // continuous interaction boundary as vanilla held processing.
        try
        {
            var rknType = AccessTools.TypeByName("RKN.Crafting.Entities.BlockEntityCraftingSurface");
            var rknStep = rknType is null ? null : AccessTools.Method(rknType, "OnCraftingStep");
            if (rknStep is not null)
                harmony.Patch(rknStep,
                    prefix: new(typeof(NativeLaborBridge), nameof(RknGate)),
                    postfix: new(typeof(NativeLaborBridge), nameof(RknStep)));
        }
        catch { /* Optional compatibility patch: RKN may be absent or change API. */ }
    }
    private static ServerCoordinator? Server(Entity e) => e.World.Side == EnumAppSide.Server
        ? e.Api.ModLoader.GetModSystem<SeraphModSystem>().Server : null;
    private static Seraph.Client.SeraphClientBridge? Client(Entity e)
        => e.Api is Vintagestory.API.Client.ICoreClientAPI api && ReferenceEquals(api.World.Player?.Entity, e)
            ? e.Api.ModLoader.GetModSystem<SeraphModSystem>().ClientBridge : null;
    private static void Accepted(Entity e, ExertionKind kind, double seconds = 1, bool discrete = true)
    {
        if (Server(e) is { } server)
        {
            if (discrete) server.ChargeAction(e, kind); else server.ChargeWork(e, kind, seconds);
        }
        else if (e is EntityPlayer player)
        { if (discrete) Client(e)?.PredictDiscreteWork(player, kind); else Client(e)?.PredictWork(player, kind, seconds); }
    }
    private static void Held(Entity e, string source, double elapsed, ExertionKind kind)
    {
        if (Server(e) is { } server) server.RecordHeldWork(e, source, elapsed, kind);
        else if (e is EntityPlayer player) Client(e)?.PredictHeldWork(player, source, elapsed, kind);
    }
    private static void End(Entity e, string source, double elapsed = 0, ExertionKind kind = ExertionKind.Standing)
    { Server(e)?.EndHeldWork(e, source, elapsed, kind); Client(e)?.EndPredictedWork(source, elapsed, kind); }
    private static bool Allowed(Entity e)
    {
        if (NativeInjuryActionBridge.Functions(e) is not { Manipulation: true, CanUseActiveHand: false }) return true;
        NativeInjuryActionBridge.Denied(e); return false;
    }
    private static bool DiscreteAllowed(Entity e) => Allowed(e)
        && (Server(e)?.DiscreteWorkAllowed(e) ?? Client(e)?.DiscreteWorkAllowed() ?? true);
    private static float Speed(Entity e) => NativeInjuryActionBridge.Functions(e) is { Manipulation: true } f
        ? (float)Math.Clamp(f.Mining, .05, 1) : 1;
    private static bool HeldGate(EntityAgent byEntity, ref float secondsUsed, ref bool __result, out float __state)
    {
        __state = secondsUsed;
        if (Allowed(byEntity)) { secondsUsed *= Speed(byEntity); return true; }
        __result = false;
        return false;
    }
    private static void HeldStep(object __instance, EntityAgent byEntity, float __state, bool __result)
    {
        if (__result) Held(byEntity, __instance.GetType().Name, __state,
            __instance is ItemFirestarter ? ExertionKind.Igniting : ExertionKind.Crafting);
        // Completion also returns false; the following stop/cancel settles the final remainder.
    }
    private static void HeldEnd(object __instance, EntityAgent byEntity, ref float secondsUsed)
    {
        var allowed = Allowed(byEntity);
        var elapsed = secondsUsed;
        secondsUsed = allowed ? secondsUsed * Speed(byEntity) : 0;
        End(byEntity, __instance.GetType().Name, allowed ? elapsed : 0,
            __instance is ItemFirestarter ? ExertionKind.Igniting : ExertionKind.Crafting);
    }
    // Hash only the native voxel grid. Avoid serializing recipes, meshes, inventories,
    // attributes and mod data twice on every accepted/rejected mouse operation.
    private static ulong VoxelState(BlockEntity entity)
    {
        var hash = 14695981039346656037UL;
        void Add(byte value) { hash = unchecked((hash ^ value) * 1099511628211UL); }
        if (entity is BlockEntityAnvil anvil) { foreach (byte value in anvil.Voxels) Add(value); }
        else if (entity is BlockEntityClayForm clay) { foreach (bool value in clay.Voxels) Add(value ? (byte)1 : (byte)0); }
        else { foreach (bool value in ((BlockEntityKnappingSurface)entity).Voxels) Add(value ? (byte)1 : (byte)0); }
        return hash;
    }
    private static bool VoxelStart(BlockEntity __instance, IPlayer byPlayer, out ulong? __state)
    {
        __state = null;
        if (!DiscreteAllowed(byPlayer.Entity)) return false;
        __state = VoxelState(__instance);
        return true;
    }
    private static void VoxelEnd(BlockEntity __instance, IPlayer byPlayer, ulong? __state)
    {
        if (__state is null || __state == VoxelState(__instance)) return;
        Accepted(byPlayer.Entity,
            __instance is BlockEntityAnvil ? ExertionKind.HeavyTool : ExertionKind.Crafting);
    }
    private sealed class ChiselTransaction(IPlayer player)
    {
        internal IPlayer Player { get; } = player;
        internal bool Changed;
    }
    [ThreadStatic] private static Stack<ChiselTransaction>? chiselTransactions;
    private static bool ChiselStart(IPlayer byPlayer, out ChiselTransaction? __state)
    {
        __state = null;
        // UpdateVoxel may be called once per voxel during one face-cut operation.
        // Gate the action on actual hand function here; apply the discrete labor
        // cooldown only when settling a changed voxel transaction below.
        if (!Allowed(byPlayer.Entity)) return false;
        __state = new(byPlayer);
        (chiselTransactions ??= new()).Push(__state);
        return true;
    }
    private static Exception? ChiselFinish(Exception? __exception, ChiselTransaction? __state)
    {
        if (__state is null) return __exception;
        var stack = chiselTransactions!;
        if (stack.Count > 0 && ReferenceEquals(stack.Peek(), __state)) stack.Pop();
        if (__state.Changed && DiscreteAllowed(__state.Player.Entity))
            Accepted(__state.Player.Entity, ExertionKind.Crafting);
        return __exception;
    }
    private static bool ChiselGate(IPlayer byPlayer, ref bool __result)
    {
        if (chiselTransactions is { Count: > 0 } && ReferenceEquals(chiselTransactions.Peek().Player, byPlayer)) return true;
        if (DiscreteAllowed(byPlayer.Entity)) return true;
        __result = false; return false;
    }
    private static void Chiseled(IPlayer byPlayer, bool __result)
    {
        if (!__result) return;
        if (chiselTransactions is { Count: > 0 } && ReferenceEquals(chiselTransactions.Peek().Player, byPlayer))
            chiselTransactions.Peek().Changed = true;
        else Accepted(byPlayer.Entity, ExertionKind.Crafting);
    }
    private static bool GrindGate(BlockEntityQuern __instance, IPlayer player, bool playerGrinding, bool ___automated)
    {
        var work = playerGrinding && !___automated && __instance.CanGrind();
        if (work && !Allowed(player.Entity)) return false;
        Client(player.Entity)?.PredictGrinding(work);
        return true;
    }
    private static void GrindTick(BlockEntityQuern __instance, ref float dt, bool ___automated, Dictionary<string, long> ___playersGrinding)
    {
        if (__instance.Api.Side != EnumAppSide.Server || ___automated || !__instance.CanGrind()) return;
        var speeds = new List<float>();
        foreach (var uid in ___playersGrinding.Keys.ToArray())
        {
            var player = __instance.Api.World.PlayerByUid(uid);
            if (player is null) continue;
            if (!Allowed(player.Entity)) { __instance.SetPlayerGrinding(player, false); continue; }
            if (__instance.Api.World.ElapsedMilliseconds - ___playersGrinding[uid] <= 1000)
            {
                speeds.Add(Speed(player.Entity));
                Server(player.Entity)?.ChargeWork(player.Entity, ExertionKind.HeavyTool, dt);
            }
        }
        dt *= speeds.Count > 0 ? speeds.Average() : 0;
    }
    private static bool CraftGate(ItemSlot __instance, ref ItemStackMoveOperation op, ref int __result)
    {
        var player = op.ActingPlayer ?? (__instance.Inventory as InventoryBasePlayer)?.Player;
        if (!LaborOutput(__instance.Itemstack) || player is null || DiscreteAllowed(player.Entity)) return true;
        op.MovedQuantity = 0;
        __result = 0;
        return false;
    }
    private static bool ContainedGate(IPlayer byPlayer, ref float secondsUsed, ref bool __result, out float __state)
    {
        __state = secondsUsed;
        if (Allowed(byPlayer.Entity)) { secondsUsed *= Speed(byPlayer.Entity); return true; }
        __result = false; return false;
    }
    private static bool ContainedStopGate(ItemSlot slot, BlockEntityContainer be, IPlayer byPlayer, ref float secondsUsed, out (ItemStack? Stack, int Size, float Seconds) __state)
    {
        __state = default;
        if (!Allowed(byPlayer.Entity)) return false;
        __state = (slot.Itemstack, slot.StackSize, secondsUsed);
        secondsUsed *= Speed(byPlayer.Entity);
        return true;
    }
    private static void ContainedStopped(ItemSlot slot, IPlayer byPlayer, (ItemStack? Stack, int Size, float Seconds) __state)
    {
        // The final native step returns false even on completion. Charge its remainder only after a committed mutation.
        if (__state.Stack is not null && (!ReferenceEquals(slot.Itemstack, __state.Stack) || slot.StackSize != __state.Size))
            Held(byPlayer.Entity, "ground-process", __state.Seconds, ExertionKind.Crafting);
        ContainedEnd(byPlayer);
    }
    private static void ContainedStep(CollectibleBehaviorGroundStoredProcessable __instance, IPlayer byPlayer, float __state, bool __result)
    {
        if (__result) Held(byPlayer.Entity, "ground-process", __state, ExertionKind.Crafting);
    }
    private static void ContainedEnd(IPlayer byPlayer) => End(byPlayer.Entity, "ground-process");
    private static bool LaborOutput(ItemStack? stack) => stack is not null
        && (stack.Collectible.Tool is not null || stack.Collectible.Code.Path.StartsWith("arrow-", StringComparison.Ordinal));
    private static void Crafted(InventoryBasePlayer __instance, GridRecipe? __result)
    {
        if (__result is not null && LaborOutput(__result.Output?.ResolvedItemStack) && __instance.Player is { } player)
            Accepted(player.Entity, ExertionKind.Crafting);
    }

    // Some 1.22 builds return void instead of the consumed recipe. Capture the
    // accepted recipe before native consumption clears/replaces MatchingRecipe.
    private static readonly FieldInfo CraftingRecipe = AccessTools.Field(
        Assembly.Load("VintagestoryLib").GetType("Vintagestory.Common.InventoryCraftingGrid", true)!, "MatchingRecipe");
    private static void CraftingStarted(object __instance, ItemSlot outputSlot, out GridRecipe? __state)
        => __state = outputSlot.Empty ? null : (GridRecipe?)CraftingRecipe.GetValue(__instance);

    private static void CraftingFinished(InventoryBasePlayer __instance, GridRecipe? __state)
        => Crafted(__instance, __state);

    private static bool PlaceGate(Block __instance, IPlayer byPlayer, ref bool __result)
    {
        // The bathtub places its two linked cells by calling DoPlaceBlock twice;
        // let its transaction finish and charge from DidPlaceBlock instead.
        if (__instance is BlockBathtub || byPlayer?.Entity is null || DiscreteAllowed(byPlayer.Entity)) return true;
        __result = false;
        NativeInjuryActionBridge.Denied(byPlayer.Entity);
        return false;
    }

    private static bool RknGate(IPlayer byPlayer, ref bool __result)
    {
        if (byPlayer?.Entity is null || Allowed(byPlayer.Entity)) return true;
        __result = false;
        return false;
    }

    private static void RknStep(float secondsUsed, IPlayer byPlayer, bool __result)
    {
        if (__result && byPlayer?.Entity is { } entity && secondsUsed > 0)
            Held(entity, "rkn-crafting-surface", secondsUsed, ExertionKind.Crafting);
    }
    public void Dispose() => harmony.UnpatchAll("vsslssmod.labor");
}
