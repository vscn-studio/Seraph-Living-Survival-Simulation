using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Seraph.World;

/// <summary>Owns the host snapshot and both render caches; callers receive isolated item stacks.</summary>
internal sealed class ChimneyHost
{
    private const string Key = "chimneyHost";
    private ItemStack? stack;
    private readonly Dictionary<int, MeshData> meshes = new();
    internal ItemStack? Snapshot => stack?.Clone();
    internal void Set(ItemStack value) { stack = value.Clone(); stack.StackSize = 1; meshes.Clear(); }
    internal void Resolve(IWorldAccessor world) { stack?.ResolveBlockOrItem(world); meshes.Clear(); }
    internal void Read(ITreeAttribute tree, IWorldAccessor world)
    {
        stack = tree.GetItemstack(Key)?.Clone();
        if (stack is not null) stack.StackSize = 1;
        Resolve(world);
    }
    internal void Write(ITreeAttribute tree)
    {
        if (stack is null) tree.RemoveAttribute(Key);
        else tree.SetItemstack(Key, stack.Clone());
    }
    internal ItemStack[] Drops(Block chimney) => stack is null
        ? [new ItemStack(chimney)] : [stack.Clone(), new ItemStack(chimney)];
    internal void Render(Block chimney, ITerrainMeshPool target, ITesselatorAPI tessellator)
    {
        foreach (var block in stack?.Block is { } host ? new[] { host, chimney } : new[] { chimney })
        {
            if (!meshes.TryGetValue(block.Id, out var mesh))
            { tessellator.TesselateBlock(block, out mesh); if (mesh is not null) meshes[block.Id] = mesh; }
            if (mesh is not null) target.AddMeshData(mesh.Clone());
        }
    }
}
