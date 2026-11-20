using System.Reflection;
using HarmonyLib;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.Client;

/// <summary>
/// Feeds fluid-layer targets into the game's existing block-info HUD.  Water is
/// not part of the solid block selection, so observing only Block.GetPlacedBlockInfo
/// cannot reliably reach a shallow pool or a water cell below its bank.
/// </summary>
internal sealed class NativeWaterInfoHud : IDisposable
{
    private const string HarmonyId = "vsslssmod.water-info-hud";
    private static NativeWaterInfoHud? active;

    private readonly ICoreClientAPI api;
    private readonly SlowCallbackProbe refreshProbe = new("client.water-info.refresh", 50);
    private readonly System.Func<BlockPos, string> waterInfo;
    private readonly System.Func<Seraph.Network.ServerSnapshot?> bedSnapshot;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly Type? hudType;
    private readonly FieldInfo? capiField;
    private readonly FieldInfo? currentBlockField;
    private readonly FieldInfo? currentPosField;
    private readonly FieldInfo? currentEntityField;
    private readonly FieldInfo? currentSelectionIndexField;
    private readonly FieldInfo? titleField;
    private readonly FieldInfo? detailField;
    private readonly MethodInfo? composeMethod;
    private readonly MethodInfo? isOpenedMethod;
    private readonly HashSet<MethodInfo> containerInfoMethods = new();
    private string? lastKey;
    private string? lastTitle;
    private string? lastDetail;
    private bool disposed;
    private long nextCheck;
    private long nextDetail;
    private string? detailKey, cachedNativeTitle, cachedSolidDetail;
    internal void RefreshSoon() => nextCheck = 0;

    internal static bool OverrideActive { get; private set; }
    private static string? titleOverride;
    private static string? detailOverride;
    private static Block? overrideBlock;
    private static BlockPos? overridePos;

    internal NativeWaterInfoHud(ICoreClientAPI api, System.Func<BlockPos, string> waterInfo,
        System.Func<Seraph.Network.ServerSnapshot?>? bedSnapshot = null)
    {
        this.api = api;
        this.waterInfo = waterInfo;
        this.bedSnapshot = bedSnapshot ?? (() => null);
        active = this;

        hudType = AccessTools.TypeByName("Vintagestory.Client.NoObf.HudElementBlockAndEntityInfo");
        var every15 = hudType is null ? null : AccessTools.Method(hudType, "Every15ms", [typeof(float)]);
        capiField = AccessTools.Field(typeof(HudElement), "capi") ?? FindField(hudType, "capi");
        currentBlockField = FindField(hudType, "currentBlock");
        currentPosField = FindField(hudType, "currentPos");
        currentEntityField = FindField(hudType, "currentEntity");
        currentSelectionIndexField = FindField(hudType, "currentSelectionIndex");
        titleField = FindField(hudType, "title");
        detailField = FindField(hudType, "detail");
        composeMethod = hudType is null ? null : AccessTools.Method(hudType, "ComposeBlockInfoHud");
        isOpenedMethod = AccessTools.Method(typeof(GuiDialog), "IsOpened")
            ?? AccessTools.PropertyGetter(typeof(GuiDialog), "IsOpened")
            ?? FindMethod(hudType, "IsOpened");

        if (every15 is null || capiField is null || currentBlockField is null || currentPosField is null || composeMethod is null)
        {
            api.Logger.Warning("[vsslssmod] Native block-info HUD target unavailable; water details use the normal block-info path.");
            return;
        }

        harmony.Patch(every15, prefix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(Every15Prefix)));
        // The native 500ms listener also calls Compose directly, outside Every15ms.
        harmony.Patch(composeMethod, prefix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(ComposePrefix)),
            postfix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(ComposePostfix)));
        var name = AccessTools.Method(typeof(Block), nameof(Block.GetPlacedBlockName), [typeof(IWorldAccessor), typeof(BlockPos)]);
        var info = AccessTools.Method(typeof(Block), nameof(Block.GetPlacedBlockInfo), [typeof(IWorldAccessor), typeof(BlockPos), typeof(IPlayer)]);
        if (name is not null) harmony.Patch(name, postfix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(BlockNamePostfix)));
        if (info is not null) harmony.Patch(info, postfix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(BlockInfoPostfix)));
        RegisterContainerTypes([typeof(BlockLiquidContainerBase), typeof(BlockBarrel)]);
        harmony.Patch(AccessTools.Method(typeof(BlockBed), nameof(BlockBed.GetPlacedBlockInfo)),
            postfix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(BedInfoPostfix)));
        harmony.Patch(AccessTools.Method(typeof(BlockBed), nameof(BlockBed.GetHeldItemInfo)),
            postfix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(BedHeldInfoPostfix)));
        // StartClientSide precedes world asset loading. Discover modded overrides
        // after loading rather than enumerating an uninitialized block registry.
        api.Event.LevelFinalize += RegisterContainers;
    }

    private void RegisterContainers() => RegisterContainerTypes(api.World.Blocks.OfType<BlockLiquidContainerBase>().Select(b => b.GetType()));

    private void RegisterContainerTypes(IEnumerable<Type> types)
    {
        foreach (var type in types)
        {
            var method = AccessTools.Method(type, nameof(Block.GetPlacedBlockInfo),
                [typeof(IWorldAccessor), typeof(BlockPos), typeof(IPlayer)]);
            if (method is null || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null
                || !containerInfoMethods.Add(method)) continue;
            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(NativeWaterInfoHud), nameof(ContainerInfoPostfix)));
            }
            catch (Exception ex)
            {
                api.Logger.Debug("[vsslssmod] Skipped liquid-container info patch for {0}: {1}", type.FullName, ex.Message);
            }
        }
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (AccessTools.DeclaredField(current, name) is { } field) return field;
        return null;
    }

    private static MethodInfo? FindMethod(Type? type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (AccessTools.DeclaredMethod(current, name) is { } method) return method;
        return null;
    }

    private static bool Every15Prefix(object __instance)
        => active?.Refresh(__instance) ?? true;

    private static void ComposePrefix(object __instance, out bool __state)
    {
        __state = OverrideActive;
        if (active is not { disposed: false } owner || owner.lastTitle is null
            || owner.currentBlockField?.GetValue(__instance) is not Block block
            || !WaterInteractionTarget.IsWaterBlock(block)) return;
        OverrideActive = true;
        titleOverride = owner.lastTitle;
        detailOverride = owner.lastDetail;
        overrideBlock = block;
        overridePos = owner.currentPosField?.GetValue(__instance) as BlockPos;
    }

    private static void ComposePostfix(bool __state) => OverrideActive = __state;

    private bool Refresh(object hud)
    {
        var now = api.World.ElapsedMilliseconds;
        if (now < nextCheck && nextCheck - now <= 100) return lastKey is null;
        nextCheck = now + 100;
        using var timing = refreshProbe?.Measure(api);
        if (disposed || capiField is null || currentBlockField is null || currentPosField is null || composeMethod is null)
            return true;
        if (capiField.GetValue(hud) is not ICoreClientAPI client || !IsOpen(hud)
            || client.World?.Player is not { Entity: { } entity } player)
        {
            ClearOverride();
            return true;
        }

        // Entity information has priority in the native HUD.
        if (player.CurrentEntitySelection is not null)
        {
            ClearOverride();
            return true;
        }

        var selection = player.CurrentBlockSelection;
        var range = player.WorldData?.PickingRange ?? 6;
        if (!TryResolveWaterLook(client, entity, selection, range, out var waterBlock, out var resolvedPos,
            out var solidBlock, out var solidPos, out var selectionIndex))
        {
            ClearOverride();
            return true;
        }

        var key = $"{resolvedPos.dimension}:{resolvedPos.X}:{resolvedPos.Y}:{resolvedPos.Z}:{solidPos}:{selectionIndex}";
        if (key != detailKey || now >= nextDetail) {
            detailKey = key; nextDetail = now + 500;
            cachedNativeTitle = GetNativeTitle(client.World, waterBlock, resolvedPos);
            cachedSolidDetail = BuildSolidDetail(client.World, player, solidBlock, solidPos);
        }
        var title = cachedNativeTitle!;
        var waterDetail = SafeWaterInfo(resolvedPos);
        var solidDetail = cachedSolidDetail;
        var detail = string.IsNullOrWhiteSpace(solidDetail) ? waterDetail
            : string.IsNullOrWhiteSpace(waterDetail) ? solidDetail
            : waterDetail + "\n" + solidDetail;

        var currentBlock = currentBlockField.GetValue(hud) as Block;
        var currentPos = currentPosField.GetValue(hud) as BlockPos;
        var currentIndex = currentSelectionIndexField?.GetValue(hud) is int index ? index : 0;
        var nativeTitle = titleField?.GetValue(hud) as string;
        var nativeDetail = detailField?.GetValue(hud) as string;
        var changed = title != lastTitle || detail != lastDetail;
        // Another client mod can recompose the native HUD between our ticks.
        // Compare its actual text fields so water details cannot stay stale.
        if (titleField is not null) changed |= nativeTitle != title;
        if (detailField is not null) changed |= nativeDetail != detail;
        if (!changed) { currentBlockField.SetValue(hud, waterBlock); currentPosField.SetValue(hud, resolvedPos);
            currentSelectionIndexField?.SetValue(hud, selectionIndex); lastKey = key; return false; }

        currentEntityField?.SetValue(hud, null);
        currentBlockField.SetValue(hud, waterBlock);
        currentPosField.SetValue(hud, resolvedPos);
        currentSelectionIndexField?.SetValue(hud, selectionIndex);
        lastKey = key;
        lastTitle = title;
        lastDetail = detail;
        OverrideActive = true;
        titleOverride = title;
        detailOverride = detail;
        overrideBlock = waterBlock;
        overridePos = resolvedPos;
        try
        {
            composeMethod.Invoke(hud, null);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[vsslssmod] Water block-info composition failed: {0}", ex.GetBaseException().Message);
        }
        finally
        {
            ClearOverrideOnly();
        }
        return false;
    }

    private bool IsOpen(object hud)
    {
        if (isOpenedMethod is null) return true;
        try { return isOpenedMethod.Invoke(hud, null) as bool? ?? true; }
        catch { return true; }
    }

    private string SafeWaterInfo(BlockPos pos)
    {
        try { return waterInfo(pos); }
        catch (Exception ex)
        {
            api.Logger.Warning("[vsslssmod] Water detail query failed: {0}", ex.GetBaseException().Message);
            return "";
        }
    }

    private static string GetNativeTitle(IWorldAccessor world, Block block, BlockPos pos)
    {
        var previous = OverrideActive;
        OverrideActive = false;
        try
        {
            var title = block.GetPlacedBlockName(world, pos);
            return string.IsNullOrWhiteSpace(title) ? "Water" : title;
        }
        catch { return "Water"; }
        finally { OverrideActive = previous; }
    }

    private static string BuildSolidDetail(IWorldAccessor world, IPlayer player, Block? solidBlock, BlockPos? solidPos)
    {
        if (solidBlock is null || solidPos is null || solidBlock.Id == 0) return "";
        var previous = OverrideActive;
        OverrideActive = false;
        try
        {
            var name = solidBlock.GetPlacedBlockName(world, solidPos);
            var info = solidBlock.GetPlacedBlockInfo(world, solidPos, player);
            if (string.IsNullOrWhiteSpace(name)) name = solidBlock.Code?.ToShortString() ?? "";
            if (string.IsNullOrWhiteSpace(info)) return name;
            return string.IsNullOrWhiteSpace(name) ? info.Trim() : name + "\n" + info.Trim();
        }
        catch { return ""; }
        finally { OverrideActive = previous; }
    }

    private static bool TryResolveWaterLook(ICoreClientAPI client, EntityPlayer entity,
        BlockSelection? selection, double range, out Block waterBlock, out BlockPos waterPos,
        out Block? solidBlock, out BlockPos? solidPos, out int selectionIndex)
    {
        waterBlock = null!;
        waterPos = null!;
        solidBlock = null;
        solidPos = null;
        selectionIndex = selection?.SelectionBoxIndex ?? 0;

        var resolved = WaterInteractionTarget.FindForHover(client.World, entity, selection, range);
        if (resolved is null) return false;
        if (selection is not null) ResolveSolidSelection(client, selection, out solidBlock, out solidPos);
        return WaterInteractionTarget.TryGetWaterAt(client.World.BlockAccessor, resolved, out waterBlock, out waterPos);
    }

    private static void ResolveSolidSelection(ICoreClientAPI client, BlockSelection selection,
        out Block? solidBlock, out BlockPos? solidPos)
    {
        solidBlock = null;
        solidPos = null;
        var pos = selection.Position.Copy();
        if (selection.DidOffset) pos.Add(selection.Face.Opposite, 1);
        var block = client.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
        if (block.Id == 0 || WaterInteractionTarget.IsWaterBlock(block)) return;
        solidBlock = block;
        solidPos = pos;
    }

    private static void ClearOverrideOnly()
    {
        OverrideActive = false;
        titleOverride = null;
        detailOverride = null;
        overrideBlock = null;
        overridePos = null;
    }

    private void ClearOverride()
    {
        lastKey = lastTitle = lastDetail = null;
        ClearOverrideOnly();
    }

    private static void BlockNamePostfix(Block __instance, BlockPos pos, ref string __result)
    {
        if (OverrideActive && ReferenceEquals(__instance, overrideBlock) && pos.Equals(overridePos)
            && titleOverride is not null) __result = titleOverride;
    }

    private static void BlockInfoPostfix(Block __instance, BlockPos pos, ref string __result)
    {
        AppendCropPollution(__instance, pos, ref __result);
        if (OverrideActive && ReferenceEquals(__instance, overrideBlock) && pos.Equals(overridePos)
            && detailOverride is not null) __result = detailOverride;
    }

    private static void AppendCropPollution(Block block, BlockPos pos, ref string result)
    {
        if (OverrideActive || active is not { disposed: false } || block is BlockLiquidContainerBase) return;
        try
        {
            var world = active.api.World;
            var farmlandPos = pos;
            var farmland = world.BlockAccessor.GetBlockEntity(farmlandPos) as BlockEntityFarmland;
            var isCrop = block is BlockCrop;
            if (farmland is null && isCrop)
            {
                farmlandPos = pos.DownCopy();
                farmland = world.BlockAccessor.GetBlockEntity(farmlandPos) as BlockEntityFarmland;
            }
            if (farmland is null) return;

            var loads = NativeCropPollution.ReadForInfo(farmland);
            var total = loads.Values.Where(double.IsFinite).Sum();
            var key = total <= 0 ? "crop-pollution-clean" : total < 0.1 ? "crop-pollution-low"
                : total < 0.35 ? "crop-pollution-moderate" : "crop-pollution-high";
            var text = LocalizedText.Get("vsslssmod:" + key, total * 100);
            if (isCrop) text += "\n" + LocalizedText.Get("vsslssmod:crop-pollution-harvest");
            result = string.IsNullOrWhiteSpace(result) ? text : result.TrimEnd() + "\n" + text;
        }
        catch (Exception ex)
        {
            active.api.Logger.Debug("[vsslssmod] Crop pollution tooltip unavailable: {0}", ex.Message);
        }
    }

    private static void BedInfoPostfix(BlockBed __instance, IWorldAccessor world, ref string __result)
    {
        __result = __result.TrimEnd() + "\n" + BedRecovery(__instance);
    }

    private static void BedHeldInfoPostfix(BlockBed __instance, IWorldAccessor world, System.Text.StringBuilder dsc)
    {
        TooltipText.AppendItem(dsc, BedRecovery(__instance));
    }

    private static string BedRecovery(Block bed)
        => BedRestText.Describe(bed, active?.bedSnapshot());

    private static void ContainerInfoPostfix(Block __instance, IWorldAccessor world, BlockPos pos, ref string __result)
    {
        if (active is not { disposed: false } || __instance is not BlockLiquidContainerBase container) return;
        // The native bucket puts its content label on a separate line. Join only
        // that exact label/value boundary, preserving other mods' descriptions.
        var label = Vintagestory.API.Config.Lang.Get("Contents:");
        __result = System.Text.RegularExpressions.Regex.Replace(__result,
            System.Text.RegularExpressions.Regex.Escape(label) + @"\r?\n[ \t]*(?=\S)", _ => label + " ");
        var liquid = container.GetContent(pos);
        if (!WaterQuality.IsWater(liquid)) return;
        // GetContent reads the block entity's synchronized inventory, with no hover round trip.
        var batch = WaterQuality.ReadLiquidAt(world, liquid, WaterMetadata.HasTag(liquid!) ? 15 : WaterQuality.Ambient(world, pos), new());
        if (batch is null) return;
        var details = string.Join("\n", WaterInfoText.Lines(batch,
            (key, args) => Seraph.LocalizedText.Get("vsslssmod:" + key, args), includeVolume: false));
        // Derived container methods may call the patched base method.
        if (!__result.Contains(details, StringComparison.Ordinal)) __result = __result.TrimEnd() + "\n" + details;
    }

    internal void Reset() { nextCheck = nextDetail = 0; detailKey = null; ClearOverride(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        api.Event.LevelFinalize -= RegisterContainers;
        ClearOverride();
        if (ReferenceEquals(active, this)) active = null;
        harmony.UnpatchAll(HarmonyId);
    }
}
