using System.Reflection;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

internal static class FixtureWaterChecks
{
    internal static void Run(string root)
    {
        var meshes = new List<MeshData>();
        var baseMesh = CubeMeshUtil.GetCube();
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var atlas = Proxy<IBlockTextureAtlasAPI>((method, args) =>
        {
            if (method.Name == "get_Size") return new Size2i(1024, 1024);
            if (method.Name == "GetOrInsertTexture")
            {
                Require(args![0]!.ToString()!.Contains("waterportion"), "Liquid mesh uses the wrong texture.");
                args[1] = 1;
                args[2] = new TextureAtlasPosition { x1 = .1f, y1 = .1f, x2 = .2f, y2 = .2f };
                return true;
            }
            throw new Exception("Unexpected atlas call " + method.Name);
        });
        var assets = Proxy<IAssetManager>((method, args) => method.Name == "TryGet"
            ? new Asset(File.ReadAllBytes(Path.Combine(root, "assets", ((AssetLocation)args![0]!).Domain,
                ((AssetLocation)args[0]!).Path)), (AssetLocation)args[0]!, null!)
            : throw new Exception("Unexpected asset call " + method.Name));
        var water = new Item { Code = new("game:waterportion"), ItemId = 100,
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100,\"texture\":{\"base\":\"game:block/liquid/waterportion\"}}}") };
        var bath = new BlockEntityBathtub { Block = new BlockBathtub { Code = new("vsslssmod:bathtub") }, Pos = new BlockPos(0, 10, 0, 0) };
        var part = new BlockEntityBathtub { Block = new BlockBathtub { Code = new("vsslssmod:bathtub-part") }, Pos = bath.Pos.AddCopy(1, 0, 0) };
        var accessor = Proxy<IBlockAccessor>((method, args) => method.Name switch
        {
            "GetBlockEntity" => ((BlockPos)args![0]!).X == 0 ? bath : part,
            "MarkBlockDirty" or "MarkBlockEntityDirty" => null,
            _ => throw new Exception("Unexpected mesh accessor call " + method.Name)
        });
        var world = Proxy<IClientWorldAccessor>((method, _) => method.Name switch
        {
            "GetItem" => water, "get_BlockAccessor" => accessor, "get_Side" => EnumAppSide.Client,
            _ => throw new Exception("Unexpected mesh world call " + method.Name)
        });
        var manager = Proxy<ITesselatorManager>((method, _) => method.Name == "GetDefaultBlockMesh" ? baseMesh
            : throw new Exception("Unexpected mesh manager call " + method.Name));
        var tesselator = new ShapeTesselator(null!, null!, null!, null!);
        var capi = Proxy<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_World" => world, "get_Assets" => assets, "get_BlockTextureAtlas" => atlas,
            "get_TesselatorManager" => manager, "get_Side" => EnumAppSide.Client,
            "get_Tesselator" => tesselator,
            _ => throw new Exception("Unexpected mesh API call " + method.Name)
        });
        var uvSource = Proxy<ITexPositionSource>((m, _) => m.Name == "get_AtlasSize" ? new Size2i(1024, 1024)
            : new TextureAtlasPosition { x1 = 0, y1 = 0, x2 = 1, y2 = 1 });
        foreach (var code in new[] { "latrine", "bathtub", "bathtub-part", "copperbathtub", "copperbathtub-part", "copperbathtub-end", "wellwall", "wellhead", "chimney" })
        {
            var shape = Shape.TryGet(capi, new AssetLocation($"vsslssmod:shapes/block/{code}.json"));
            foreach (var element in shape.Elements.Where(e => e.Name?.StartsWith("voxel_") == true || e.Name?.StartsWith("cell_") == true))
            {
                if (code is "latrine" or "wellhead")
                {
                    Require(element.FacesResolved is { } faces && faces.Where(face => face is not null).All(face =>
                        face!.Uv is { Length: 4 } uv && uv.All(value => float.IsFinite(value) && value is >= 0 and <= 16)
                        && face.Rotation % 90 == 0),
                        $"{code}/{element.Name} contains an invalid calibrated source UV.");
                    continue;
                }
                tesselator.TesselateShape(code + "-uv", new Shape { Elements = [element], TextureWidth = 16, TextureHeight = 16 }, out var uvMesh, uvSource);
                for (var v = 0; v < uvMesh.VerticesCount; v++)
                {
                    var x = uvMesh.xyz[v * 3]; var y = uvMesh.xyz[v * 3 + 1]; var z = uvMesh.xyz[v * 3 + 2];
                    if (code == "chimney") y -= int.Parse(element.Name!.Split('_')[1]) - 1;
                    // Face order is the engine's native N/E/S/W/U/D order.
                    var expected = (v / 4) switch {
                        0 => (1-x, 1-y), 1 => (1-z, 1-y), 2 => (x, 1-y),
                        3 => (z, 1-y), 4 => (1-x, 1-z), _ => (1-x, z) };
                    Require(Math.Abs(uvMesh.Uv[v * 2] - expected.Item1) < 1e-5
                        && Math.Abs(uvMesh.Uv[v * 2 + 1] - expected.Item2) < 1e-5,
                        $"{code}/{element.Name} face {v/4} UV differs from native microblock mapping at {x},{y},{z}: {uvMesh.Uv[v*2]},{uvMesh.Uv[v*2+1]} expected {expected}.");
                }
            }
        }
        Console.WriteLine("PASS fixture UVs checked against native mapping; authored latrine/wellhead UVs stay calibrated and in-bounds");
        var pool = Proxy<ITerrainMeshPool>((method, args) =>
        {
            Require(method.Name == "AddMeshData", "Unexpected pool call.");
            meshes.Add((MeshData)args![0]!); return null;
        });
        bath.Api = capi; part.Api = capi;
        Require(bath.OnTesselation(pool, tesselator) && meshes.Count == 1, "Empty bath lost its oriented structure or added water.");
        foreach (var volume in new[] { 10d, 100d, 200d })
        {
            var tree = new TreeAttribute(); tree.SetString("seraphFormat", "seraph.release.bath"); tree.SetInt("seraphVersion", 1); tree.SetString("waterFormat", "seraph.release.water"); tree.SetInt("waterQualityVersion", 1); tree.SetDouble("waterVolume", volume);
            bath.FromTreeAttributes(tree, world);
            var partTree = new TreeAttribute(); partTree.SetString("seraphFormat", "seraph.release.bath"); partTree.SetInt("seraphVersion", 1); partTree.SetDouble("visualWaterVolume", volume);
            part.FromTreeAttributes(partTree, world);
            MeshData? left = null;
            foreach (var entity in new[] { bath, part })
            {
                meshes.Clear();
                Require(entity.OnTesselation(pool, tesselator), "Filled bath did not render.");
                CheckMeshes(meshes, baseMesh);
                var mesh = meshes[1];
                var expectedHeight = .125f + .625f * (float)(volume / 200);
                Require(Coordinates(mesh, 1).All(y => Math.Abs(y - expectedHeight) < 1e-6), "Bath water height did not follow its volume.");
                var shell = Shape.TryGet(capi, new AssetLocation($"vsslssmod:shapes/block/{(entity == bath ? "bathtub" : "bathtub-part")}.json"));
                for (var x = 0; x < 16; x++)
                    for (var z = 0; z < 16; z++)
                    {
                        var solid = shell.Elements.Any(e => e.From![0] < x + .5 && e.To![0] > x + .5
                            && e.From![2] < z + .5 && e.To![2] > z + .5
                            && e.From![1] <= expectedHeight * 16 - .001 && e.To![1] > expectedHeight * 16 - .001);
                        var wet = Enumerable.Range(0, mesh.VerticesCount / 4).Any(q =>
                        {
                            var xs = Enumerable.Range(q * 4, 4).Select(v => mesh.xyz[v * 3] * 16).ToArray();
                            var zs = Enumerable.Range(q * 4, 4).Select(v => mesh.xyz[v * 3 + 2] * 16).ToArray();
                            return xs.Min() < x + .5 && xs.Max() > x + .5 && zs.Min() < z + .5 && zs.Max() > z + .5;
                        });
                        Require(!solid || !wet, "Bath water crosses its exported wall.");
                        if (z is >= 5 and <= 10 && (entity == bath ? x >= 4 : x <= 11))
                            Require(solid || wet, "Bath interior has a dry gap.");
                    }
                if (entity == bath) left = mesh;
                else Require(Coordinates(left!, 0).Max() == Coordinates(mesh, 0).Min() + 1,
                    "Water surfaces do not join between the two bath cells.");
            }
        }
        // A directional head must draw even with no bucket, without rotating the shared native mesh.
        var head = new BlockEntityWellHead { Block = new BlockWellHead { Code = new("vsslssmod:wellhead") }, Pos = bath.Pos.Copy() };
        var originalHead = (float[])baseMesh.xyz.Clone();
        for (var direction = 0; direction < 4; direction++)
        {
            var tree = new TreeAttribute(); tree.SetString("seraphFormat", "seraph.release.well"); tree.SetInt("seraphVersion", 1); tree.SetInt("direction", direction);
            head.Api = null!; head.FromTreeAttributes(tree, world); head.Api = capi;
            meshes.Clear();
            Require(head.OnTesselation(pool, tesselator) && meshes.Count == 1, "Empty wellhead disappeared on tessellation.");
            var expected = baseMesh.Clone().Rotate(new Vec3f(.5f, 0, .5f), 0, -direction * GameMath.PIHALF, 0);
            Require(meshes[0].xyz.SequenceEqual(expected.xyz) && baseMesh.xyz.SequenceEqual(originalHead),
                "Wellhead render disagrees with saved direction or mutates the shared native mesh.");
            var cached = meshes[0]; meshes.Clear(); head.OnTesselation(pool, tesselator);
            Require(ReferenceEquals(cached, meshes[0]), "Unchanged wellhead rebuilt its oriented mesh.");
        }
        Console.WriteLine("PASS wellhead native mesh in four saved directions, empty-bucket rendering, shared mesh isolation and cache reuse");
        foreach (var kind in new[] { "well", "treatment", "bath" })
        {
            BlockEntity Broken() => kind == "well" ? new BlockEntityWellHead() : kind == "bath" ? new BlockEntityBathtub() : new BlockEntityWaterTreatment();
            var tree = new TreeAttribute(); tree.SetString("seraphFormat", "seraph.release." + kind); tree.SetInt("seraphVersion", 1);
            if (kind == "well") tree.SetDouble("storageL", -1);
            else if (kind == "bath") tree.SetDouble("waterVolume", -1);
            else tree.SetString("treatmentSlots", "[null,{}]");
            var rejected = Broken(); rejected.Pos = bath.Pos;
            try { rejected.FromTreeAttributes(tree, world); throw new Exception("Corrupt fixture accepted: " + kind); }
            catch (InvalidDataException) { }
            var untouched = new TreeAttribute(); untouched.SetString("marker", "preserved");
            try { rejected.ToTreeAttributes(untouched); throw new Exception("Rejected fixture allowed save: " + kind); }
            catch (InvalidDataException) { }
            Require(untouched.Count == 1 && untouched.GetString("marker") == "preserved", "Rejected fixture modified save target.");
        }
        Console.WriteLine("PASS corrupt release well, bath and treatment data reject load and preserve save target");
        Console.WriteLine("PASS bath water tessellation matches well liquid pass/shader attributes, retained fixture mesh, shared height and joined surfaces");
        // Route texture lookup through a test atlas but tessellate the actual source shapes
        // and generated media/liquid faces with the game's native ShapeTesselator.
        Action? duringTreatmentMesh = null;
        var treatmentTesselator = Proxy<ITesselatorAPI>((m, a) =>
        {
            if (m.Name == "GetTextureSource") return uvSource;
            if (m.Name == "TesselateShape")
            {
                if ((string)a![0]! == "filter-water") duringTreatmentMesh?.Invoke();
                tesselator.TesselateShape((string)a![0]!, (Shape)a[1]!, out var output, (ITexPositionSource)a[3]!);
                a[2] = output; return null;
            }
            throw new Exception("Unexpected treatment tessellation " + m.Name);
        });
        foreach (var code in new[] { "filterpot", "drainpot", "drainpot-pipe" })
        {
            var kind = code == "filterpot" ? 1 : code == "drainpot" ? 2 : 3;
            foreach (var half in new[] { 0, 1 })
                for (var d = 0; d < 4; d++)
                foreach (var volume in kind == 3 ? new[] { 0d } : new[] { .01, 5, 10, 0 })
                {
                    var fixture = new BlockEntityWaterTreatment { Api = capi, Block = new BlockWaterTreatment { Code = new("vsslssmod:watertreatment") }, Pos = bath.Pos };
                    var state = "{\"Kind\":" + kind + ",\"Direction\":" + d + ",\"OwnerUid\":\"test\"";
                    if (kind != 3) state += ",\"Water\":{\"Format\":\"seraph.release.water\",\"QualityVersion\":1,\"VolumeL\":" + volume.ToString(System.Globalization.CultureInfo.InvariantCulture) + "},\"Cloth\":{\"Code\":\"game:linen-normal-down\",\"Class\":" + (int)EnumItemClass.Block + ",\"Quantity\":1}";
                    if (kind == 1) state += ",\"Gravel\":{\"Code\":\"game:gravel-granite\",\"Class\":" + (int)EnumItemClass.Block + ",\"Quantity\":1},\"Charcoal\":{\"Code\":\"game:charcoal\",\"Class\":" + (int)EnumItemClass.Item + ",\"Quantity\":2}";
                    state += "}";
                    var tree = new TreeAttribute(); tree.SetString("seraphFormat", "seraph.release.treatment"); tree.SetInt("seraphVersion", 1); tree.SetString("treatmentSlots", half == 0 ? "[" + state + ",{}]" : "[{}," + state + "]");
                    fixture.FromTreeAttributes(tree, world);
                    meshes.Clear(); fixture.OnTesselation(pool, treatmentTesselator);
                    Require(meshes.Count == (kind == 1 ? 4 : kind == 2 ? 2 : 1) + (volume > 0 ? 1 : 0), "Treatment fixture lost walls, media or water: " + code);
                    Require(meshes.All(m => m.VerticesCount > 0), "Treatment contour produced an empty mesh.");
                    Require(meshes.All(m => Coordinates(m, 1).All(y => y >= half * .5f - .0001 && y <= (half + 1) * .5f + .0001)), "Treatment mesh escaped its half block.");
                    if (kind != 3 && volume > 0)
                    {
                        var liquid = meshes[^1];
                        Require(liquid.RenderPassesAndExtraBits.Take(liquid.RenderPassCount).All(p => (p & 0x3ff) == 4)
                            && liquid.CustomInts?.Count == liquid.VerticesCount && liquid.CustomFloats?.Count == liquid.VerticesCount * 2,
                            "Treatment water is missing native liquid shader/pass data.");
                        Require(Coordinates(liquid, 1).All(y => Math.Abs(y - ((kind == 1 ? 3.4f + 3f * (float)(volume / 10) : 2.1f + 4.3f * (float)(volume / 10)) / 16 + half * .5f)) < 1e-6), "Treatment liquid height is wrong.");
                    }
                    if (kind != 3 && volume > 0)
                    {
                        tree.SetString("treatmentSlots", tree.GetString("treatmentSlots").Replace("\"VolumeL\":" + volume.ToString(System.Globalization.CultureInfo.InvariantCulture), "\"VolumeL\":0"));
                        fixture.FromTreeAttributes(tree, world);
                        meshes.Clear(); fixture.OnTesselation(pool, treatmentTesselator);
                        Require(meshes.Count == (kind == 1 ? 4 : 2), "Synced empty pot retained its previous liquid mesh.");
                    }
                }
        }
        // Drain synchronization can arrive while a terrain worker is finishing
        // the old wet snapshot. That worker must not cache its wet result forever.
        var updating = new BlockEntityWaterTreatment { Api = capi, Block = new BlockWaterTreatment { Code = new("vsslssmod:watertreatment") }, Pos = bath.Pos };
        var concurrentWet = new TreeAttribute(); concurrentWet.SetString("seraphFormat", "seraph.release.treatment"); concurrentWet.SetInt("seraphVersion", 1); concurrentWet.SetString("treatmentSlots", "[{\"Kind\":2,\"Water\":{\"Format\":\"seraph.release.water\",\"QualityVersion\":1,\"VolumeL\":1}},{}]");
        var dry = new TreeAttribute(); dry.SetString("seraphFormat", "seraph.release.treatment"); dry.SetInt("seraphVersion", 1); dry.SetString("treatmentSlots", "[{\"Kind\":2},{}]");
        updating.FromTreeAttributes(concurrentWet, world);
        duringTreatmentMesh = () => { duringTreatmentMesh = null; updating.FromTreeAttributes(dry, world); };
        meshes.Clear(); updating.OnTesselation(pool, treatmentTesselator);
        meshes.Clear(); updating.OnTesselation(pool, treatmentTesselator);
        Require(meshes.Count == 1, "A concurrent drain update left a cached wet mesh.");
        Console.WriteLine("PASS drain update during native water tessellation cannot retain an old water mesh");
        foreach (var code in new[] { "filterpot", "drainpot" })
        {
            var display = Shape.TryGet(capi, new AssetLocation($"vsslssmod:shapes/block/{code}-inventory.json"));
            Require(display.Elements.All(e => Enumerable.Range(0, 3).All(a => e.From![a] >= 0 && e.To![a] <= 16)), "Treatment inventory model clips its display cube.");
        }
        Console.WriteLine("PASS native treatment wall/media/liquid tessellation in both halves and four directions, shader data and complete inventory shapes");
        var inventory = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/bathtub-inventory.json"));
        var firstCell = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/bathtub.json"));
        var secondCell = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/bathtub-part.json"));
        Require(inventory.Elements.Length == firstCell.Elements.Length + secondCell.Elements.Length,
            "Bath inventory shape lost one of its world cells.");
        for (var axis = 0; axis < 3; axis++)
            Require(inventory.Elements.All(e => e.From![axis] >= 0 && e.To![axis] <= 16),
                "Bath inventory shape extends outside its display cube.");
        Require(inventory.Elements.Any(e => e.From![0] < 8) && inventory.Elements.Any(e => e.To![0] > 8),
            "Bath inventory shape contains only half of the assembly.");
        var copperInventory = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/copperbathtub-inventory.json"));
        var copperFirst = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/copperbathtub.json"));
        var copperSecond = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/copperbathtub-part.json"));
        var copperEnd = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/copperbathtub-end.json"));
        Require(copperInventory.Elements.Length == copperFirst.Elements.Length + copperSecond.Elements.Length + copperEnd.Elements.Length,
            "Copper bath inventory shape lost part of its three-cell model.");
        Require(copperInventory.Elements.All(e => Enumerable.Range(0, 3).All(a => e.From![a] >= 0 && e.To![a] <= 16)),
            "Copper bath inventory model extends outside its display cube.");
        Require(new[] { copperFirst, copperSecond, copperEnd }.All(s => s.Elements.All(e => !(e.Name ?? "").Contains("foot", StringComparison.OrdinalIgnoreCase))),
            "Copper bath model retained support feet.");
        Require(new[] { copperFirst, copperSecond, copperEnd }.All(s => Math.Abs(s.Elements.Min(e => e.From![1])) < 1e-8),
            "Copper bath must rest on the ground in all three cells.");
        var copperShapes = new[] { copperSecond, copperFirst, copperEnd };
        foreach (var direction in Enumerable.Range(0, 4))
            foreach (var volume in new[] { 1d, 150d, 300d })
            {
                var liquids = new List<MeshData>();
                for (int cell = 0; cell < 3; cell++)
                {
                    var code = cell switch { 0 => "copperbathtub-part", 1 => "copperbathtub", _ => "copperbathtub-end" };
                    var fixture = new BlockEntityBathtub { Api = capi, Pos = bath.Pos.Copy(),
                        Block = new BlockBathtub { Code = new("vsslssmod:" + code) } };
                    var tree = new TreeAttribute();
                    tree.SetString("seraphFormat", "seraph.release.bath"); tree.SetInt("seraphVersion", 1);
                    tree.SetString("waterFormat", "seraph.release.water"); tree.SetInt("waterQualityVersion", 1);
                    tree.SetInt("direction", direction); tree.SetDouble("waterVolume", cell == 1 ? volume : 0);
                    tree.SetDouble("visualWaterVolume", volume);
                    fixture.FromTreeAttributes(tree, world);
                    meshes.Clear(); fixture.OnTesselation(pool, tesselator); CheckMeshes(meshes, baseMesh);
                    var liquid = meshes[1].Clone().Rotate(new Vec3f(.5f, 0, .5f), 0, direction * GameMath.PIHALF, 0);
                    var height = (.75f + 8.25f * (float)(volume / 300)) / 16;
                    Require(Coordinates(liquid, 1).All(y => Math.Abs(y - height) < 1e-6),
                        "Copper water does not follow its grounded floor and rim.");
                    Require(Coordinates(liquid, 2).Min() > .09 && Coordinates(liquid, 2).Max() < .91,
                        "Copper water extends through the widened side walls.");
                    for (int q = 0; q < liquid.VerticesCount / 4; q++)
                    {
                        double x = Enumerable.Range(q * 4, 4).Average(v => liquid.xyz[v * 3] * 16);
                        double z = Enumerable.Range(q * 4, 4).Average(v => liquid.xyz[v * 3 + 2] * 16);
                        Require(!copperShapes[cell].Elements.Any(e => e.From![0] < x && e.To![0] > x
                            && e.From![2] < z && e.To![2] > z && e.From![1] < height * 16 && e.To![1] > height * 16),
                            "Copper water crosses the imported shell.");
                    }
                    liquids.Add(liquid);
                }
                for (int cell = 0; cell < 2; cell++)
                    Require(Math.Abs(Coordinates(liquids[cell], 0).Max() - Coordinates(liquids[cell + 1], 0).Min() - 1) < 1e-5,
                        "Copper water leaves a seam between adjacent cells.");
            }
        Console.WriteLine("PASS copper water meshes at low/half/full levels in four directions, grounded floor, wall clearance and three-cell joins");
        Require(!File.Exists(Path.Combine(root, "assets/vsslssmod/blocktypes/washbasin.json"))
            && !File.Exists(Path.Combine(root, "assets/vsslssmod/recipes/grid/washbasin.json")),
            "Removed wash basin remains available as a block or recipe.");
        Console.WriteLine("PASS two-cell wooden and three-cell copper bath inventory models fit inside their display cubes");
        foreach (var code in new[] { "wellwall", "wellhead" })
        {
            var worldShape = Shape.TryGet(capi, new AssetLocation($"vsslssmod:shapes/block/{code}.json"));
            var itemShape = Shape.TryGet(capi, new AssetLocation($"vsslssmod:shapes/block/{code}-inventory.json"));
            Require(itemShape.Elements.All(e => Enumerable.Range(0, 3).All(axis => e.From![axis] >= 0 && e.To![axis] <= 16)),
                $"{code} inventory model clips its display cube.");
        }
        Console.WriteLine("PASS well-wall and well-head inventory shapes fit their display cube");
        var wall = new BlockEntityWellWall { Block = new Block { Code = new("vsslssmod:wellwall") }, Pos = bath.Pos, Api = capi };
        meshes.Clear();
        Require(!wall.OnTesselation(pool, tesselator) && meshes.Count == 0, "Dry well wall replaced its default mesh.");
        foreach (var height in new[] { .03f, .5f, .97f })
        {
            var tree = new TreeAttribute(); tree.SetBool("waterVisible", true); tree.SetFloat("waterHeight", height);
            wall.FromTreeAttributes(tree, world);
            meshes.Clear();
            Require(wall.OnTesselation(pool, tesselator), "Well water did not render.");
            CheckMeshes(meshes, baseMesh);
            Require(Coordinates(meshes[1], 1).All(y => Math.Abs(y - height) < 1e-6), "Well water did not follow its synced height.");
        }
        var wallShape = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/wellwall.json"));
        var surfaceShape = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/well-wall-water.json"));
        bool Covers(ShapeElement e, double x, double z) => e.From![0] < x && e.To![0] > x && e.From![2] < z && e.To![2] > z;
        for (var x = 0; x < 16; x++)
            for (var z = 0; z < 16; z++)
            {
                var solid = wallShape.Elements.Any(e => Covers(e, x + .5, z + .5));
                var waterFaces = surfaceShape.Elements.Count(e => Covers(e, x + .5, z + .5));
                Require(waterFaces == (solid ? 0 : 1), $"Well water crosses the stone boundary or leaves a hole at {x},{z}.");
            }
        wall.FromTreeAttributes(new TreeAttribute(), world);
        meshes.Clear();
        Require(!wall.OnTesselation(pool, tesselator) && meshes.Count == 0, "Drained well retained an old water surface.");
        Console.WriteLine("PASS native well wall retained with animated liquid, exact stone/water boundary and synced water removal");
    }

    private static IEnumerable<float> Coordinates(MeshData mesh, int axis) =>
        Enumerable.Range(0, mesh.VerticesCount).Select(i => mesh.xyz[i * 3 + axis]);

    private static void CheckMeshes(List<MeshData> meshes, MeshData baseMesh)
    {
        Require(meshes.Count == 2 && meshes[0].VerticesCount == baseMesh.VerticesCount, "Water rendering discarded the fixture walls.");
        var mesh = meshes[1];
        Require(mesh.VerticesCount > 0 && mesh.RenderPassesAndExtraBits.Take(mesh.RenderPassCount)
            .All(p => (p & 0x3ff) == (short)EnumChunkRenderPass.Liquid), "Water did not use its designated liquid pass.");
        Require(mesh.CustomInts?.Count == mesh.VerticesCount && mesh.CustomFloats?.Count == mesh.VerticesCount * 2,
            "Native liquid shader attributes are missing.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}


