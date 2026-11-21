using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Preserves native interactions and item-count semantics; only pure water is adapted.</summary>
internal sealed partial class NativeWaterTransfer : IDisposable
{
    private const string PatchId = "vsslssmod.water";
    private static readonly Dictionary<BlockLiquidContainerBase, ICoreAPI> Apis = new();
    private static Harmony? harmony;
    private static int owners;
    private readonly ICoreAPI api;
    [ThreadStatic] private static WaterBatch? fillingSource;
    [ThreadStatic] private static WellFillContext? fillingWell;
    [ThreadStatic] private static bool routingBarrel;

    private sealed record WellFillContext(Server.ServerCoordinator Owner, BlockPos Position);

    public NativeWaterTransfer(ICoreAPI api)
    {
        this.api = api;
        if (owners++ > 0) return;
        harmony = new(PatchId);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), nameof(CollectibleObject.GetMergableQuantity),
                [typeof(ItemStack), typeof(ItemStack), typeof(EnumMergePriority)]),
            postfix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(WaterStackMergableQuantity)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), nameof(CollectibleObject.TryMergeStacks),
                [typeof(ItemStackMergeOperation)]),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(WaterStackMerge)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ItemSlotWatertight), "ActivateSlotLeftClick"),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(PourIntoCookingSlot)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ItemSlotWatertight), "ActivateSlotRightClick"),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(DrawFromCookingSlot)));
        var type = typeof(BlockLiquidContainerBase);
        harmony.Patch(AccessTools.DeclaredMethod(type, nameof(BlockLiquidContainerBase.OnHeldInteractStart)),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(BathtubInteract)));
        harmony.Patch(AccessTools.Method(type, nameof(BlockLiquidContainerBase.TryPutLiquid),
            [typeof(ItemStack), typeof(ItemStack), typeof(float)]),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(PutHeld)));
        harmony.Patch(AccessTools.Method(type, nameof(BlockLiquidContainerBase.TryPutLiquid),
            [typeof(BlockPos), typeof(ItemStack), typeof(float)]),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(PutPlaced)));
        harmony.Patch(AccessTools.Method(type, nameof(BlockLiquidContainerBase.TryFillFromBlock),
            [typeof(ItemSlot), typeof(EntityAgent), typeof(BlockPos)]),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(BeginFill)),
            finalizer: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(EndFill)));
        harmony.Patch(AccessTools.Method(type, nameof(BlockLiquidContainerBase.GetNutritionPropertiesPerLitre)),
            postfix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(DrinkableWater)));
        harmony.Patch(AccessTools.DeclaredMethod(type, "tryEatStop"),
            prefix: new HarmonyMethod(typeof(NativeWaterTransfer), nameof(NativeDrinkStop)));
    }

    private static void WaterStackMergableQuantity(ItemStack sinkStack, ItemStack sourceStack,
        EnumMergePriority priority, ref int __result)
    {
        if (!WaterQuality.IsWater(sinkStack) || !WaterQuality.IsWater(sourceStack)) return;
        try
        {
            if (WaterMetadata.HasTag(sinkStack)) _ = WaterMetadata.Read(sinkStack, 0);
            if (WaterMetadata.HasTag(sourceStack)) _ = WaterMetadata.Read(sourceStack, 0);
        }
        catch (InvalidDataException) { __result = 0; return; }
        var sink = ComparableWater(sinkStack);
        var source = ComparableWater(sourceStack);
        if (!sink.Collectible.Equals(sink, source, GlobalConstants.IgnoredStackAttributes)) { __result = 0; return; }
        __result = Math.Max(__result, Math.Max(0,
            Math.Min(sinkStack.Collectible.MaxStackSize - sinkStack.StackSize, sourceStack.StackSize)));
    }

    private static ItemStack ComparableWater(ItemStack stack)
    {
        var copy = stack.Clone();
        foreach (var key in new[] { WaterMetadata.Key, WaterMetadata.TemperatureUpdatedAtKey, "temperature", NativeWaterHeating.RemainderKey })
            copy.Attributes.RemoveAttribute(key);
        return copy;
    }

    public void RegisterBlocks()
    {
        foreach (var block in api.World.Blocks.OfType<BlockLiquidContainerBase>()) Apis[block] = api;
    }

    public void Dispose()
    {
        foreach (var block in Apis.Where(pair => ReferenceEquals(pair.Value, api)).Select(pair => pair.Key).ToArray())
            Apis.Remove(block);
        if (--owners == 0) { harmony?.UnpatchAll(PatchId); harmony = null; }
    }

    private static bool Enabled(BlockLiquidContainerBase block, out ICoreAPI api)
    {
        if (!Apis.TryGetValue(block, out api!)) return false;
        return api.Side == EnumAppSide.Server
            && api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig.Mode == "survival";
    }

    private static bool BathtubInteract(BlockLiquidContainerBase __instance, EntityAgent byEntity,
        ItemSlot itemslot, BlockSelection? blockSel, bool firstEvent, ref EnumHandHandling handHandling)
    {
        // The placed container may ask the held item to handle the same interaction.
        // Let its own transfer path continue without starting another held action.
        if (routingBarrel) return false;
        // Native liquid containers consume the held interaction before an ordinary
        // block receives it. Route our reservoirs; Shift retains native placement.
        if (!__instance.AllowHeldLiquidTransfer || byEntity.Controls.ShiftKey || blockSel is null
            || byEntity is not EntityPlayer { Player: { } player }) return true;
        var target = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);
        if (target is BlockBarrel barrel && itemslot.Itemstack is { } held
            && Apis.TryGetValue(__instance, out var api)
            && (api.Side == EnumAppSide.Client || Enabled(__instance, out _))
            && byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityBarrel { Sealed: false }
            && WaterQuality.IsWater(__instance.GetContent(held)) && WaterQuality.IsWater(barrel.GetContent(blockSel.Position)))
        {
            handHandling = EnumHandHandling.PreventDefault;
            // The native placed-barrel interaction transfers the actual amount and
            // checks claims. Do not predict inventory changes on the client.
            if (api.Side == EnumAppSide.Server)
            {
                routingBarrel = true;
                try { barrel.OnBlockInteractStart(byEntity.World, player, blockSel); }
                finally { routingBarrel = false; }
            }
            return false;
        }
        if (target is not BlockBathtub) return true;
        handHandling = EnumHandHandling.PreventDefault;
        // The server's start packet uses firstEvent=false; step callbacks do not enter here.
        if (firstEvent || byEntity.World.Side == EnumAppSide.Server)
            target.OnBlockInteractStart(byEntity.World, player, blockSel);
        return false;
    }

    private static void DrinkableWater(BlockLiquidContainerBase __instance, ItemStack itemstack,
        ref FoodNutritionProperties? __result)
    {
        // Vanilla plain water has no nutrition descriptor, so its existing drinking animation needs
        // a zero-nutrition descriptor. This adds no satiety, nutrition, healing or alcohol.
        if (__result is null && __instance.CanDrinkFrom && WaterQuality.IsWater(__instance.GetContent(itemstack))
            && Apis.TryGetValue(__instance, out var api)
            && (api.Side == EnumAppSide.Client || Enabled(__instance, out _)))
            __result = new() { Satiety = 0, FoodCategory = EnumFoodCategory.Unknown };
    }

    private static bool NativeDrinkStop(BlockLiquidContainerBase __instance, float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        if (slot.Itemstack is null || !Apis.TryGetValue(__instance, out var api)
            || !WaterQuality.IsWater(__instance.GetContent(slot.Itemstack))) return true;
        if (api.Side == EnumAppSide.Client) return true;
        if (!Enabled(__instance, out _)) return true;
        if (secondsUsed >= 0.95f && byEntity is EntityPlayer { Player: IServerPlayer player })
            api.ModLoader.GetModSystem<SeraphModSystem>().DrinkFromContainer(player, slot, __instance);
        return false;
    }

    // Vintage Story 1.22.7 names this native parameter `itemslot`. Harmony binds
    // auxiliary parameters by name, so keeping the exact API name is required.
    private static bool BeginFill(BlockLiquidContainerBase __instance, ItemSlot itemslot, EntityAgent byEntity,
        BlockPos pos, out WaterBatch? __state, ref bool __result)
    {
        __state = fillingSource;
        if (Enabled(__instance, out var api)
            && api.ModLoader.GetModSystem<SeraphModSystem>().Server is { } owner
            && IsWellTarget(api.World, pos)
            && TryFillFromVirtualWell(__instance, itemslot, byEntity, pos, owner))
        {
            __result = true;
            return false;
        }
        fillingSource = Enabled(__instance, out api)
            ? WaterQuality.WorldSource(api.World, pos, api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig) : null;
        return true;
    }

    private static Exception? EndFill(Exception? __exception, WaterBatch? __state)
    {
        fillingSource = __state;
        return __exception;
    }

    private static bool PutHeld(BlockLiquidContainerBase __instance, ItemStack containerStack,
        ItemStack liquidStack, float desiredLitres, ref int __result)
    {
        if (!Apis.TryGetValue(__instance, out var api)
            || api.Side != EnumAppSide.Client && !Enabled(__instance, out _)
            || !WaterQuality.IsWater(liquidStack)) return true;
        __result = 0;
        var content = __instance.GetContent(containerStack);
        var capacity = (containerStack.Collectible as ILiquidSink)?.CapacityLitres ?? 0;
        var replacement = Mixture(api, content, liquidStack, desiredLitres, capacity, 15, out var moved);
        if (replacement is not null)
        {
            if (fillingWell is { } well && !well.Owner.TryExtractWell(well.Position, moved / (double)
                BlockLiquidContainerBase.GetContainableProps(liquidStack)!.ItemsPerLitre)) return false;
            __instance.SetContent(containerStack, replacement);
            __result = moved;
        }
        return false;
    }

    private static bool TryFillFromVirtualWell(BlockLiquidContainerBase container, ItemSlot slot,
        EntityAgent byEntity, BlockPos pos, Server.ServerCoordinator owner)
    {
        if (slot.Itemstack is null || slot.StackSize != 1) return false;
        var sink = slot.Itemstack.Collectible as ILiquidSink;
        var capacity = sink?.CapacityLitres ?? container.CapacityLitres;
        if (!float.IsFinite(capacity) || capacity <= 0) return false;
        if (!owner.TryGetWellSupply(pos, capacity, out var batch, out var drawL)) return false;
        var api = Apis[container];
        var item = api.World.GetItem(new AssetLocation("game:waterportion"));
        if (item is null) return false;
        var props = BlockLiquidContainerBase.GetContainableProps(new ItemStack(item));
        if (props is not { Containable: true, ItemsPerLitre: > 0 }) return false;
        var count = (int)Math.Ceiling(drawL * props.ItemsPerLitre);
        if (count <= 0) return false;
        var source = new ItemStack(item, count);
        var previous = fillingSource;
        var previousWell = fillingWell;
        var moved = 0;
        try
        {
            fillingSource = batch;
            fillingWell = new(owner, pos.Copy());
            moved = container.TryPutLiquid(slot.Itemstack, source, (float)drawL);
        }
        finally
        {
            fillingSource = previous;
            fillingWell = previousWell;
        }
        if (moved <= 0) return false;
        if (byEntity is EntityPlayer { Player: IServerPlayer player }) owner.SendWellFillReveal(player, pos);
        return true;
    }

    private static bool IsWellTarget(IWorldAccessor world, BlockPos pos)
    {
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var block = world.BlockAccessor.GetBlock(pos.AddCopy(dx, 0, dz));
                if (block.Code?.Domain == "vsslssmod"
                    && block.Code.Path is "wellhead" or "wellwall") return true;
            }
        return false;
    }

    private static bool PutPlaced(BlockLiquidContainerBase __instance, BlockPos pos,
        ItemStack liquidStack, float desiredLitres, ref int __result)
    {
        if (!Apis.TryGetValue(__instance, out var api)
            || api.Side != EnumAppSide.Client && !Enabled(__instance, out _)
            || !WaterQuality.IsWater(liquidStack)) return true;
        __result = 0;
        if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityContainer) return false;
        var replacement = Mixture(api, __instance.GetContent(pos), liquidStack, desiredLitres,
            __instance.CapacityLitres, WaterQuality.Ambient(api.World, pos), out var moved);
        if (replacement is not null) { SetPlacedContent(api.World, __instance, pos, replacement); __result = moved; }
        return false;
    }

    private static ItemStack? Mixture(ICoreAPI api, ItemStack? current, ItemStack source,
        float desiredL, float capacityL, double ambient, out int moved)
    {
        moved = 0;
        var props = BlockLiquidContainerBase.GetContainableProps(source);
        if (props is not { Containable: true } || !float.IsFinite(props.ItemsPerLitre) || props.ItemsPerLitre <= 0
            || !float.IsFinite(desiredL) || desiredL <= 0 || !float.IsFinite(capacityL) || capacityL <= 0
            || capacityL > 10000 || source.StackSize <= 0
            || source.Collectible.GetTransitionableProperties(api.World, source, null) is { Length: > 0 }) return null;
        var units = (double)props.ItemsPerLitre;
        var currentCount = current?.StackSize ?? 0;
        if (currentCount < 0 || current is not null && (!WaterQuality.IsWater(current)
            || BlockLiquidContainerBase.GetContainableProps(current)?.ItemsPerLitre != props.ItemsPerLitre
            || current.Collectible.GetTransitionableProperties(api.World, current, null) is { Length: > 0 })) return null;
        if (current is not null)
        {
            if (!WaterIdentityMatches(api.World, current, source)) return null;
        }
        var requested = Math.Floor(Math.Min(desiredL, capacityL) * units + 1e-5);
        var room = Math.Floor(capacityL * units + 1e-5) - currentCount;
        var count = Math.Min(source.StackSize, Math.Min(requested, room));
        if (count <= 0 || count > int.MaxValue - currentCount) return null;
        var incoming = source.Clone(); incoming.StackSize = (int)count;
        var config = api.ModLoader.GetModSystem<SeraphModSystem>().InteractionConfig;
        var batch = fillingSource is not null && !WaterMetadata.HasTag(source)
            ? fillingSource with { VolumeL = count / units }
            : WaterQuality.ReadLiquidAt(api.World, incoming, ambient, config);
        if (batch is null || batch.Validate() is not null) return null;
        if (current is not null)
        {
            var existing = WaterQuality.ReadLiquidAt(api.World, current, ambient, config);
            if (existing is null) return null;
            batch = existing.Mix(batch);
        }
        var item = api.World.GetItem(new AssetLocation(batch.SaltGramsPerL > 5 ? "game:saltwaterportion" : "game:waterportion"));
        if (item is null || BlockLiquidContainerBase.GetContainableProps(new ItemStack(item))?.ItemsPerLitre != props.ItemsPerLitre) return null;
        var result = new ItemStack(item, currentCount + (int)count) { Attributes = (current ?? source).Attributes.Clone() };
        // Fractional evaporation is a heating accumulator, not water identity or
        // an extra volume. Do not clone its pending charge into a new reservoir.
        result.Attributes.RemoveAttribute(NativeWaterHeating.RemainderKey);
        WaterMetadata.WriteAt(api.World, result, batch);
        moved = (int)count;
        return result;
    }

    internal static bool WaterIdentityMatches(IWorldAccessor world, ItemStack current, ItemStack incoming)
    {
        // Water quality and its temperature-update clock describe stored water;
        // neither makes otherwise identical water a different liquid for pouring.
        // Keep all other attributes as a merge barrier for third-party liquids.
        var left = new ItemStack(incoming.Collectible) { Attributes = current.Attributes.Clone() };
        var right = incoming.Clone();
        // The native collectible temperature is a physical state, not liquid
        // identity. Vanilla writes it whenever water is heated or cools, so it
        // must not prevent two otherwise compatible water stacks from merging.
        foreach (var key in new[] { WaterMetadata.Key, WaterMetadata.TemperatureUpdatedAtKey, "temperature", NativeWaterHeating.RemainderKey })
        {
            left.Attributes.RemoveAttribute(key);
            right.Attributes.RemoveAttribute(key);
        }
        return left.Equals(world, right, GlobalConstants.IgnoredStackAttributes);
    }

    internal static void SetPlacedContent(IWorldAccessor world, BlockLiquidContainerBase container, BlockPos pos, ItemStack? content)
    {
        // Native SetContent(BlockPos, ...) performs a slot transfer, not replacement.
        var entity = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityContainer
            ?? throw new InvalidOperationException("Liquid container entity missing.");
        var slot = entity.Inventory[container.GetContainerSlotId(pos)];
        slot.Itemstack = content; slot.MarkDirty(); entity.MarkDirty(true);
    }
}
