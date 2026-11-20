using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Seraph.World;

internal static class FixtureOrientation
{
    [ThreadStatic] internal static int? PlacementDirection;
    internal static IDisposable Placement(int direction) => new PlacementScope(direction);
    private sealed class PlacementScope : IDisposable
    {
        private readonly int? previous = PlacementDirection;
        internal PlacementScope(int direction) { PlacementDirection = direction & 3; }
        public void Dispose() { PlacementDirection = previous; }
    }
    // Direction indices: north, east, south, west. Use the actual camera vector:
    // the native placement-facing helper can intentionally point back at the placer.
    internal static int Facing(IPlayer player)
    {
        var v = EntityPos.GetViewVector(0, player.Entity.Pos.Yaw);
        return Math.Abs(v.X) > Math.Abs(v.Z) ? v.X > 0 ? 1 : 3 : v.Z > 0 ? 2 : 0;
    }
    internal static Cuboidf Rotate(Cuboidf box, int turns)
    {
        var b = box.Clone();
        for (var i = 0; i < (turns & 3); i++) b = new(1 - b.Z2, b.Y1, b.X1, 1 - b.Z1, b.Y2, b.X2);
        return b;
    }
    internal static Cuboidf[][] Boxes(Cuboidf[] boxes) => Enumerable.Range(0, 4)
        .Select(d => boxes.Select(b => Rotate(b, d)).ToArray()).ToArray();
    internal static MeshData Mesh(MeshData mesh, int turns) => mesh.Clone()
        .Rotate(new Vec3f(.5f, 0, .5f), 0, -(turns & 3) * GameMath.PIHALF, 0);
    internal static BlockPos BathPartner(BlockPos pos, int turns, bool part)
    {
        (int x, int z) = (turns & 3) switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
        return pos.AddCopy(part ? -x : x, 0, part ? -z : z);
    }
    internal static BlockPos BathOffset(BlockPos pos, int turns, int distance)
    {
        (int x, int z) = (turns & 3) switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
        return pos.AddCopy(x * distance, 0, z * distance);
    }
}
