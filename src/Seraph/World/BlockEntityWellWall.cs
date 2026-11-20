using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>
/// Projects the virtual well water onto exactly one continuous wall block. It
/// never creates a liquid block or changes terrain; the server owns the level.
/// </summary>
public sealed class BlockEntityWellWall : BlockEntity
{
    private const string WaterShapePath = "vsslssmod:shapes/block/well-wall-water.json";
    private float waterHeight;
    private bool waterVisible;
    private Shape? waterShape;
    private long serverListener;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
        {
            serverListener = RegisterGameTickListener(_ => RefreshServerState(), 2000);
            RefreshServerState();
        }
        else if (api is ICoreClientAPI capi)
        {
            waterShape = Shape.TryGet(capi, new AssetLocation(WaterShapePath));
        }
    }

    private void RefreshServerState()
    {
        if (Api is null || Block is null) return;
        var bounds = WellStructureResolver.TryFromWall(Api.World.BlockAccessor, Pos, out var resolved) ? resolved : (WellStructureResolver.Bounds?)null;
        var head = bounds is { } well ? Api.World.BlockAccessor.GetBlockEntity(well.Head) as BlockEntityWellHead : null;
        var fill = head?.FillFraction ?? 0;
        var nextVisible = false;
        var nextHeight = 0f;
        if (bounds is { } valid && fill > 0.001 && Pos.Y >= valid.BottomY && Pos.Y <= valid.TopY
            && Pos.Y == Math.Clamp((int)Math.Floor(valid.BottomY + fill * valid.Depth - 0.001), valid.BottomY, valid.TopY))
        {
            nextVisible = true;
            nextHeight = Math.Clamp((float)(valid.BottomY + fill * valid.Depth - Pos.Y), 0.03f, 0.97f);
        }
        if (nextVisible == waterVisible && Math.Abs(nextHeight - waterHeight) < 0.005f) return;
        waterVisible = nextVisible;
        waterHeight = nextHeight;
        MarkDirty(true);
        Api.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        if (!waterVisible || Block is null || Api is not ICoreClientAPI capi) return false;
        waterShape ??= Shape.TryGet(capi, new AssetLocation(WaterShapePath));
        if (waterShape?.Elements is null || waterShape.Elements.Length == 0) return false;
        var water = capi.World.GetItem(new AssetLocation("game", "waterportion"));
        if (water is null) return false;
        var content = new ItemStack(water);
        var props = BlockLiquidContainerBase.GetContainableProps(content);
        if (props?.Texture is null) return false;
        var waterSource = new ContainerTextureSource(capi, content, props.Texture);
        tesselator.TesselateShape("vsslssmod-well-wall-water", waterShape,
            out var mesh, waterSource, null, props.GlowLevel);
        if (mesh is null || mesh.VerticesCount == 0) return false;
        var baseMesh = capi.TesselatorManager.GetDefaultBlockMesh(Block);
        if (baseMesh is not { VerticesCount: > 0 }) return false;
        FixtureWaterMesh.Finish(mesh, capi, Pos, props);
        // Returning true replaces the default block mesh, so retain the stone walls explicitly.
        mesher.AddMeshData(baseMesh);
        mesher.AddMeshData(mesh.Translate(0, waterHeight, 0));
        return true;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBool("waterVisible", waterVisible);
        tree.SetFloat("waterHeight", waterHeight);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        waterVisible = tree.GetBool("waterVisible", false);
        waterHeight = Math.Clamp(tree.GetFloat("waterHeight", 0), 0, 1);
        if (Api is ICoreClientAPI) MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    public override void OnBlockRemoved()
    {
        if (serverListener != 0) UnregisterGameTickListener(serverListener);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        if (serverListener != 0) UnregisterGameTickListener(serverListener);
        base.OnBlockUnloaded();
    }

}
