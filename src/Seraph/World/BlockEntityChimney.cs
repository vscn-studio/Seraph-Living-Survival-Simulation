using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.World;

/// <summary>A bottom-installed flue embedded in a full host block.</summary>
public sealed class BlockChimney : Block
{
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel,
        EntitySelection? entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        handling = EnumHandHandling.PreventDefault;
        if (!firstEvent || slot.Empty || blockSel is null || byEntity is not EntityPlayer actor) return;
        if (actor.World.Side != EnumAppSide.Server || blockSel.Face != BlockFacing.DOWN) return;
        var location = blockSel.Position.Copy();
        var owner = actor.Player;
        if (owner is null || !actor.World.Claims.TryAccess(owner, location, EnumBlockAccessFlags.BuildOrBreak)) return;
        if (!CanEmbedAt(actor.World, location, out var original, out var reason))
        {
            if (owner is IServerPlayer serverPlayer) serverPlayer.SendIngameError(reason, Lang.Get(reason));
            return;
        }
        var plan = new ChimneyInstallationPlan(location, original);
        plan.Execute(actor.World.BlockAccessor, this, slot, () => {
            actor.World.BlockAccessor.TriggerNeighbourBlockUpdate(location);
            actor.World.PlaySoundAt(new AssetLocation("game:sounds/block/stonecrack"),
                location.X + .5, location.Y + .5, location.Z + .5);
        });
    }

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack,
        BlockSelection blockSel, ref string failureCode)
    { failureCode = "vsslssmod:chimney-embed-only"; return false; }

    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    { failureCode = "vsslssmod:chimney-embed-only"; return false; }

    public static bool CanEmbedAt(IWorldAccessor world, BlockPos pos, out Block host, out string failure)
    {
        host = world.BlockAccessor.GetBlock(pos);
        failure = "vsslssmod:chimney-embed-only";
        if (!IsFullSolidCube(host) || world.BlockAccessor.GetBlockEntity(pos) is not null) return false;
        if (!ChimneyGeometry.Opening(world.BlockAccessor.GetBlock(pos.UpCopy()), BlockFacing.DOWN))
        { failure = "vsslssmod:chimney-need-air"; return false; }
        // The flue reaches two blocks above its host; a solid cap in that
        // visible outlet seals it even while the first cell remains air.
        if (!ChimneyGeometry.Opening(world.BlockAccessor.GetBlock(pos.AddCopy(0, 2, 0)), BlockFacing.DOWN))
        { failure = "vsslssmod:chimney-need-air"; return false; }
        if (!ChimneyGeometry.Opening(world.BlockAccessor.GetBlock(pos.DownCopy()), BlockFacing.UP))
        { failure = "vsslssmod:chimney-need-space-below"; return false; }
        return true;
    }

    public static bool IsFullSolidCube(Block? host) => ChimneyGeometry.FullHost(host);

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        => world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChimney entity
            ? entity.HostDrops(this) : [new ItemStack(this)];

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    { base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo); dsc.AppendLine(Lang.Get("vsslssmod:block-chimney-help")); }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var lines = new List<string> { base.GetPlacedBlockInfo(world, pos, forPlayer) };
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChimney { HostStack: { } host })
            lines.Add(Lang.Get("vsslssmod:chimney-host", host.GetName()));
        lines.Add(Lang.Get("vsslssmod:chimney-vent"));
        string Opening(BlockPos location, BlockFacing face)
        {
            var block = world.BlockAccessor.GetBlock(location);
            return Lang.Get(ChimneyGeometry.Opening(block, face)
                ? "vsslssmod:chimney-clear" : "vsslssmod:chimney-blocked");
        }
        lines.Add(Lang.Get("vsslssmod:chimney-openings", Opening(pos.DownCopy(), BlockFacing.UP), Opening(pos.AddCopy(0, 2, 0), BlockFacing.DOWN)));
        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public override int GetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type)
        => api?.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityChimney { HostStack.Block: { } host }
            ? host.GetRetention(pos, facing, type) : base.GetRetention(pos, facing, type);

    // Retain cleanup support for the previous release's companion cells.
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        for (var y = 1; y <= 2; y++)
            if (IsPart(world.BlockAccessor.GetBlock(pos.AddCopy(0, y, 0))))
                world.BlockAccessor.SetBlock(0, pos.AddCopy(0, y, 0));
        base.OnBlockRemoved(world, pos);
    }
    internal static bool IsMain(Block? block) => block?.Code?.Domain == "vsslssmod" && block.Code.Path == "chimney";
    internal static bool IsPart(Block? block) => block?.Code?.Domain == "vsslssmod"
        && block.Code.Path is "chimney-middle" or "chimney-top";
}

public sealed class BlockChimneyPart : Block
{
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f) => [];

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
        => new(world.BlockAccessor.GetBlock(new AssetLocation("vsslssmod", "chimney")));

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        for (var y = -2; y <= 0; y++)
        {
            if (!BlockChimney.IsMain(world.BlockAccessor.GetBlock(pos.AddCopy(0, y, 0)))) continue;
            world.BlockAccessor.SetBlock(0, pos.AddCopy(0, y, 0));
            for (var offset = 1; offset <= 2; offset++)
            {
                var other = pos.AddCopy(0, y + offset, 0);
                if (!other.Equals(pos) && BlockChimney.IsPart(world.BlockAccessor.GetBlock(other)))
                    world.BlockAccessor.SetBlock(0, other);
            }
            break;
        }
        base.OnBlockRemoved(world, pos);
    }
}

/// <summary>Stores the embedded host and draws its mesh together with the flue.</summary>
public sealed class BlockEntityChimney : BlockEntity
{
    private readonly ChimneyHost host = new();
    public ItemStack? HostStack => host.Snapshot;
    internal ItemStack[] HostDrops(Block chimney) => host.Drops(chimney);
    public void SetHostStack(ItemStack stack) { host.Set(stack); MarkDirty(true); }
    public override void Initialize(ICoreAPI api) { base.Initialize(api); host.Resolve(api.World); }
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        host.Read(tree, worldForResolving);
        if (Api?.Side == EnumAppSide.Client) MarkDirty(true);
    }
    public override void ToTreeAttributes(ITreeAttribute tree) { base.ToTreeAttributes(tree); host.Write(tree); }
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    { host.Render(Block, mesher, tesselator); return true; }
}
