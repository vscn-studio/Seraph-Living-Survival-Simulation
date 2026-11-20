using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

internal static class FixtureWaterMesh
{
    internal static void Finish(MeshData mesh, ICoreClientAPI api, BlockPos pos, WaterTightContainableProps props)
    {
        // Container water uses subdued waves/foam, not the world-ocean displacement.
        mesh.CustomInts = new CustomMeshDataPartInt(mesh.VerticesCount) { Count = mesh.VerticesCount };
        Array.Fill(mesh.CustomInts.Values, (1 << 28) | (1 << 29));
        mesh.CustomFloats = new CustomMeshDataPartFloat(mesh.VerticesCount * 2) { Count = mesh.VerticesCount * 2 };
        for (var i = 0; i < mesh.FlagsCount; i++) mesh.Flags[i] &= ~(1 << 12);
        if (props.ClimateColorMap is null) return;
        var tint = ColorUtil.ToBGRABytes(api.World.ApplyColorMapOnRgba(props.ClimateColorMap, null, -1,
            pos.X, pos.Y, pos.Z));
        for (var i = 0; i < mesh.VerticesCount * 4; i++)
            mesh.Rgba[i] = (byte)(mesh.Rgba[i] * tint[i % 4] / 255);
    }
}
