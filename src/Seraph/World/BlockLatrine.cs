using Seraph.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.World;

public sealed class BlockLatrine : Block
{
    private static readonly Cuboidf[] Selection = [new(0, 0, 0, 1, 0.125f, 1)];
    private static readonly Cuboidf[] StructureBoxes =
    [
        new(0, 0, 0, 0.3125f, 0.125f, 1),
        new(0.6875f, 0, 0, 1, 0.125f, 1)
    ];

    private static readonly Cuboidf[][] OrientedBoxes = FixtureOrientation.Boxes(StructureBoxes);
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        => OrientedBoxes[FixtureOrientation.PlacementDirection ?? (blockAccessor.GetBlockEntity(pos) as BlockEntityLatrine)?.Direction ?? 0];

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        using var placement = FixtureOrientation.Placement(FixtureOrientation.Facing(byPlayer));
        if (!base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode)) return false;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityLatrine latrine)
        { latrine.Direction = FixtureOrientation.Facing(byPlayer); latrine.MarkDirty(true); }
        return true;
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => Selection;

    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => false;

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
        => Lang.Get("vsslssmod:block-latrine");

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (blockSel is not null && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityLatrine latrine)
            return latrine.OnInteract(byPlayer);
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var baseText = base.GetPlacedBlockInfo(world, pos, forPlayer);
        return world.BlockAccessor.GetBlockEntity(pos) is BlockEntityLatrine latrine
            ? baseText + (string.IsNullOrWhiteSpace(baseText) ? "" : "\n") + latrine.InfoText()
            : baseText;
    }
}

public sealed partial class BlockEntityLatrine : BlockEntity
{
    // Retain old saves until their contents can be moved into a valid barrel without loss.
    private int waste;
    internal int Direction { get; set; }
    private MeshData? orientedMesh;
    private int meshDirection = -1;
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        if (Api is not ICoreClientAPI capi) return false;
        if (orientedMesh is null || meshDirection != Direction)
        {
            var mesh = capi.TesselatorManager.GetDefaultBlockMesh(Block);
            if (mesh is null) return false;
            orientedMesh = FixtureOrientation.Mesh(mesh, Direction); meshDirection = Direction;
        }
        mesher.AddMeshData(orientedMesh);
        return true;
    }

    public bool OnInteract(IPlayer player)
    {
        if (Api?.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return true;
        var owner = Api.ModLoader.GetModSystem<SeraphModSystem>().Server;
        if (owner is null) return true;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)
            || !Api.World.Claims.TryAccess(player, Pos.AddCopy(0, -1, 0), EnumBlockAccessFlags.Use)) return true;
        var storage = LatrineWasteStorage.Read(Api.World, Pos);
        if (storage is null)
        {
            serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("vsslssmod:latrine-needs-barrel"), EnumChatType.Notification);
            return true;
        }
        if (waste > 0)
        {
            var move = Math.Min(waste, storage.Capacity - storage.Quantity);
            if (move > 0)
            {
                storage.Add(Api.World, move);
                waste -= move;
                MarkDirty(true);
            }
            if (waste > 0)
            {
                serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("vsslssmod:latrine-full"), EnumChatType.Notification);
                return true;
            }
        }
        if (serverPlayer.Entity?.Controls.ShiftKey == true)
        {
            owner.TryBeginLatrineDefecation(serverPlayer, this);
            return true;
        }
        serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("vsslssmod:latrine-use-help"), EnumChatType.Notification);
        return true;
    }

    internal static bool ContainsWaste(BlockEntity? entity) => entity is BlockEntityLatrine latrine
        && (latrine.waste > 0 || latrine.Api is not null
            && ManureGroundStorage.ContainsManure(latrine.Api.World.BlockAccessor.GetBlockEntity(latrine.Pos.AddCopy(0, -1, 0))));
    public string InfoText() => Api is not null && LatrineWasteStorage.Read(Api.World, Pos) is { } storage
        ? Lang.Get("vsslssmod:latrine-level", storage.Quantity, storage.Capacity)
        : Lang.Get("vsslssmod:latrine-needs-barrel");

    public override void ToTreeAttributes(ITreeAttribute tree) { base.ToTreeAttributes(tree); tree.SetInt("waste", waste); tree.SetInt("direction", Direction); }
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        waste = Math.Clamp(tree.GetInt("waste", 0), 0, 12); Direction = tree.GetInt("direction", 0) & 3;
        orientedMesh = null;
        if (Api?.Side == EnumAppSide.Client) worldAccessForResolve.BlockAccessor.MarkBlockDirty(Pos);
    }
}
