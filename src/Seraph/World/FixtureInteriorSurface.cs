using Vintagestory.API.Common;

namespace Seraph.World;

/// <summary>Horizontal water faces bounded by the exported voxel walls at the current level.</summary>
internal static class FixtureInteriorSurface
{
    internal static Shape Build(Shape template, IReadOnlyList<Shape> cells, float height, int cellIndex)
    {
        var width = cells.Count * 16;
        var y = height * 16 - .001;
        var walls = cells.SelectMany((shape, cell) => shape.Elements
            .Where(e => e.From![1] <= y && e.To![1] > y)
            .Select(e => (X1: e.From![0] + cell * 16, X2: e.To![0] + cell * 16,
                Z1: e.From![2], Z2: e.To![2]))).ToArray();
        // Keep authored fractional wall edges. Integer voxels let water cross
        // 1.5-pixel copper walls even though the flood-fill cells were inside.
        var xs = Enumerable.Range(0, width + 1).Select(x => (double)x)
            .Concat(walls.SelectMany(w => new[] { Math.Clamp(w.X1, 0, width), Math.Clamp(w.X2, 0, width) }))
            .Distinct().Order().ToArray();
        var zs = Enumerable.Range(0, 17).Select(z => (double)z)
            .Concat(walls.SelectMany(w => new[] { Math.Clamp(w.Z1, 0, 16), Math.Clamp(w.Z2, 0, 16) }))
            .Distinct().Order().ToArray();
        int nx = xs.Length - 1, nz = zs.Length - 1;
        var solid = new bool[nx, nz];
        var inside = new bool[nx, nz];
        for (int x = 0; x < nx; x++)
            for (int z = 0; z < nz; z++)
            {
                double cx = (xs[x] + xs[x + 1]) / 2, cz = (zs[z] + zs[z + 1]) / 2;
                solid[x, z] = walls.Any(w => w.X1 <= cx && w.X2 > cx && w.Z1 <= cz && w.Z2 > cz);
            }
        var queue = new Queue<(int X, int Z)>();
        queue.Enqueue((Array.FindIndex(xs, x => x >= width / 2d), Array.FindIndex(zs, z => z >= 8)));
        while (queue.TryDequeue(out var p))
        {
            if (p.X < 0 || p.X >= nx || p.Z < 0 || p.Z >= nz || solid[p.X, p.Z] || inside[p.X, p.Z]) continue;
            inside[p.X, p.Z] = true;
            queue.Enqueue((p.X - 1, p.Z)); queue.Enqueue((p.X + 1, p.Z));
            queue.Enqueue((p.X, p.Z - 1)); queue.Enqueue((p.X, p.Z + 1));
        }
        // Never flood the outside if a malformed source model has an open wall.
        if (Enumerable.Range(0, nx).Any(x => inside[x, 0] || inside[x, nz - 1])
            || Enumerable.Range(0, nz).Any(z => inside[0, z] || inside[nx - 1, z]))
            return new Shape { Elements = [], TextureWidth = 16, TextureHeight = 16 };
        var faces = new List<ShapeElement>();
        for (var z = 0; z < nz; z++)
            for (var x = 0; x < nx; x++)
            {
                if (xs[x] < cellIndex * 16 || xs[x + 1] > (cellIndex + 1) * 16 || !inside[x, z]) continue;
                var start = x;
                while (x + 1 < nx && xs[x + 2] <= (cellIndex + 1) * 16 && inside[x + 1, z]) x++;
                var e = template.Clone().Elements[0];
                double left = xs[start] - cellIndex * 16, right = xs[x + 1] - cellIndex * 16;
                e.From = [left, -1, zs[z]]; e.To = [right, 0, zs[z + 1]];
                // The native ShapeElement clone shares face objects. Give each row
                // its own UVs instead of changing every previously generated row.
                var face = e.FacesResolved![4];
                e.FacesResolved[4] = new ShapeElementFace { Texture = face.Texture, Enabled = true,
                    Uv = [(float)left, (float)zs[z], (float)right, (float)zs[z + 1]], ReflectiveMode = face.ReflectiveMode };
                faces.Add(e);
            }
        return new Shape { Elements = faces.ToArray(), TextureWidth = 16, TextureHeight = 16 };
    }
}


