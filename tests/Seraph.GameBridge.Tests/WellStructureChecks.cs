using System.Reflection;
using Vintagestory.API.Datastructures;
using System.Text.Json;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class WellStructureChecks
{
    internal static void Run(string root, string gamePath)
    {
        var head = new Block { Code = new("vsslssmod:wellhead"), BlockId = 1 };
        var wall = new Block { Code = new("vsslssmod:wellwall"), BlockId = 2 };
        var support = new Block { Code = new("game:rock-granite"), BlockId = 3,
            MatterState = EnumMatterState.Solid, SideSolid = new SmallBoolArray(63) };
        var air = new Block { Code = new("game:air"), BlockId = 0 };
        Block foundation = support;
        var accessor = (IBlockAccessor)RoutingProxy.Make(typeof(IBlockAccessor), (method, args) =>
        {
            if (method.Name != "GetBlock") throw new Exception("Unexpected well call " + method.Name);
            var p = (BlockPos)args![0]!;
            return p.Y == 20 ? head : p.Y is 18 or 19 ? wall : p.Y == 17 ? foundation : air;
        });
        var pos = new BlockPos(10, 20, 10, 0);
        var resolver = typeof(BlockBathtub).Assembly.GetType("Seraph.World.WellStructureResolver", true)!;
        bool Resolve(string method, BlockPos target) => (bool)resolver.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [accessor, target, null])!;
        Require(Resolve("TryFromHead", pos),
            "Supported well rejected.");
        foreach (var missing in new[] { air, new Block { BlockId = 4, MatterState = EnumMatterState.Liquid },
            new Block { BlockId = 5, MatterState = EnumMatterState.Solid, SideSolid = new SmallBoolArray(0) } })
        {
            foundation = missing;
            Require(!Resolve("TryFromHead", pos)
                && !Resolve("TryFromWall", pos.AddCopy(0, -1, 0)),
                "Well remained usable without a solid foundation.");
        }
        foundation = support;
        Require(Resolve("TryFromWall", pos.AddCopy(0, -1, 0)), "Restored foundation ignored.");
        var strata = support;
        var loaded = true;
        var terrain = 12;
        var strataLayers = 2;
        var loadedChunk = (IWorldChunk)RoutingProxy.Make(typeof(IWorldChunk), (_, _) => throw new Exception("Protection must not scan chunks."));
        var strataAccessor = (IBlockAccessor)RoutingProxy.Make(typeof(IBlockAccessor), (method, args) => method.Name switch {
            "GetTerrainMapheightAt" => terrain,
            "GetChunkAtBlockPos" => loaded ? loadedChunk : null,
            "GetBlock" => ((BlockPos)args![0]!).Y < strataLayers ? strata : air,
            _ => throw new Exception("Unexpected protected-intake call " + method.Name)
        });
        var protectedBounds = new WellStructureResolver.Bounds(pos, 0, 19, 20);
        Require(WellStructureResolver.ProtectedIntake(strataAccessor, protectedBounds), "Deep isolated natural intake was not protected.");
        terrain = 11;
        Require(!WellStructureResolver.ProtectedIntake(strataAccessor, protectedBounds), "A shallow well received deep protection.");
        terrain = 12; strataLayers = 1;
        Require(!WellStructureResolver.ProtectedIntake(strataAccessor, protectedBounds), "One rock layer counted as two.");
        strataLayers = 2; strata = new Block { Code = new("game:stonebricks-granite"), BlockId = 9 };
        Require(!WellStructureResolver.ProtectedIntake(strataAccessor, protectedBounds), "Built masonry counted as natural isolation.");
        strata = support; loaded = false;
        Require(!WellStructureResolver.ProtectedIntake(strataAccessor, protectedBounds), "Unloaded geology granted protection.");
        using var block = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/blocktypes/wellwall.json")));
        Require(!block.RootElement.TryGetProperty("collisionBoxes", out _)
            && !block.RootElement.TryGetProperty("selectionBoxes", out _), "Well wall retained hollow collision overrides.");
        Require(wall.GetCollisionBoxes(accessor, pos.AddCopy(0, -1, 0)).Any(b => b.X1 <= .5 && b.X2 >= .5 && b.Z1 <= .5 && b.Z2 >= .5),
            "Native well wall collision does not cover its center.");
        JsonElement ReadAsset(string relative)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)));
            return document.RootElement.Clone();
        }
        (float Min, float Max) VerticalBounds(string relative)
        {
            var elements = ReadAsset(relative).GetProperty("elements").EnumerateArray();
            var min = float.MaxValue; var max = float.MinValue;
            foreach (var element in elements)
            {
                min = Math.Min(min, element.GetProperty("from")[1].GetSingle());
                max = Math.Max(max, element.GetProperty("to")[1].GetSingle());
            }
            return (min, max);
        }
        Require(VerticalBounds("assets/vsslssmod/shapes/block/latrine.json") == (0, 2),
            "Latrine render geometry no longer matches the two-voxel blueprint height.");
        Require(VerticalBounds("assets/vsslssmod/shapes/block/wellhead.json") == (0, 27),
            "Wellhead geometry was not restored to the two-cell blueprint scale.");
        var wellheadShape = ReadAsset("assets/vsslssmod/shapes/block/wellhead.json");
        Require(!wellheadShape.GetProperty("elements").EnumerateArray()
                .Any(e => e.GetProperty("name").GetString()!.StartsWith("rope-coil-", StringComparison.Ordinal)),
            "Wellhead retained the decorative rope coils around its wooden spindle.");
        foreach (var model in new[] { "latrine", "wellhead" })
        {
            foreach (var element in ReadAsset($"assets/vsslssmod/shapes/block/{model}.json").GetProperty("elements").EnumerateArray())
            {
                foreach (var face in element.GetProperty("faces").EnumerateObject())
                {
                    Require(face.Value.GetProperty("uv").ValueKind == JsonValueKind.Array
                        && face.Value.GetProperty("uv").GetArrayLength() == 4,
                        $"{model} {element.GetProperty("name").GetString()} {face.Name} lost BuildWright's exported UV.");
                    if (face.Value.GetProperty("texture").GetString() == "#woodud")
                        Require(face.Value.TryGetProperty("rotation", out var rotation) && rotation.GetInt32() == 90,
                            $"{model} {element.GetProperty("name").GetString()} {face.Name} lost BuildWright's UV rotation.");
                }
            }
        }
        foreach (var item in new[] { "soap", "soap-powder", "ash", "seasalt" })
        {
            var itemJson = ReadAsset("assets/vsslssmod/itemtypes/" + item + ".json");
            var behavior = itemJson.GetProperty("behaviors").EnumerateArray()
                .Single(value => value.GetProperty("name").GetString() == "GroundStorable");
            Require(behavior.GetProperty("properties").GetProperty("collisionBox").GetProperty("y2").GetSingle() == .125f,
                item + " does not use the vanilla low-profile ground-storage collision.");
            var expectedGuiScale = item == "soap" ? 4.9f : 2.54f;
            Require(itemJson.GetProperty("guiTransform").GetProperty("scale").GetSingle() == expectedGuiScale
                && itemJson.GetProperty("groundTransform").GetProperty("scale").GetSingle() == 4.5f,
                item + " does not use the vanilla bread/potash preview scale.");
        }
        var salt = ReadAsset("assets/vsslssmod/itemtypes/seasalt.json");
        Require(salt.GetProperty("shape").GetProperty("base").GetString() == "game:item/resource/crushed/normal"
            && salt.GetProperty("textures").GetProperty("quartz").GetProperty("base").GetString() == "vsslssmod:item/seasalt"
            && File.Exists(Path.Combine(root, "assets/vsslssmod/textures/item/seasalt.png"))
            && salt.GetProperty("maxstacksize").GetInt32() == 64
            && salt.GetProperty("creativeinventory").GetProperty("vsslssmod")[0].GetString() == "*"
            && salt.GetProperty("behaviors")[0].GetProperty("properties").GetProperty("layout").GetString() == "Messy12"
            && salt.GetProperty("attributes").GetProperty("dissolveInWater").GetBoolean(),
            "Sea salt lost native shape texture slot, mod tab, stack size or vanilla ground-storage behavior.");
        Require(ReadAsset("assets/game/lang/zh-cn.json").GetProperty("tabname-vsslssmod").GetString() == "炽天使：生存模拟",
            "Creative tab does not use the full mod name.");
        var ash = ReadAsset("assets/vsslssmod/itemtypes/ash.json");
        Require(ash.GetProperty("texture").GetProperty("base").GetString() == "vsslssmod:item/ash"
            && ash.GetProperty("attributes").GetProperty("inContainerTexture").GetProperty("base").GetString() == "vsslssmod:item/ash"
            && ash.GetProperty("attributes").GetProperty("fertilizerProps").GetProperty("p").GetInt32() == 4
            && ash.GetProperty("attributes").GetProperty("fertilizerProps").GetProperty("k").GetInt32() == 22
            && ash.GetProperty("attributes").GetProperty("dissolveInWater").GetBoolean()
            && File.Exists(Path.Combine(root, "assets/vsslssmod/textures/item/ash.png")),
            "Ash texture, fertilizer value, or water solubility is incorrect.");
        var lye = ReadAsset("assets/vsslssmod/itemtypes/lye.json");
        Require(lye.GetProperty("class").GetString() == "ItemLiquidPortion"
            && lye.GetProperty("texture").GetProperty("base").GetString() == "vsslssmod:block/liquid/lye"
            && lye.GetProperty("attributes").GetProperty("waterTightContainerProps").GetProperty("texture").GetProperty("base").GetString()
                == "vsslssmod:block/liquid/lye"
            && File.Exists(Path.Combine(root, "assets/vsslssmod/textures/block/liquid/lye.png")),
            "Lye is not a pale-yellow liquid portion.");
        var lyeRecipe = ReadAsset("assets/vsslssmod/recipes/barrel/lye.json");
        Require(lyeRecipe.GetProperty("sealHours").GetInt32() == 24
            && lyeRecipe.GetProperty("ingredients")[0].GetProperty("quantity").GetInt32() == 3
            && lyeRecipe.GetProperty("ingredients")[1].GetProperty("litres").GetInt32() == 5
            && lyeRecipe.GetProperty("output").GetProperty("litres").GetInt32() == 5,
            "Ash lye barrel recipe has an incorrect three-ash, five-litre, 24-hour process.");
        var soapRecipes = ReadAsset("assets/vsslssmod/recipes/barrel/soap.json").EnumerateArray().ToArray();
        Require(soapRecipes.Length == 2 && soapRecipes.All(r => r.GetProperty("sealHours").GetInt32() == 12)
            && soapRecipes.All(r => r.GetProperty("ingredients").GetArrayLength() == 2
                && r.GetProperty("output").GetProperty("code").GetString() == "vsslssmod:soap")
            && soapRecipes.All(r => r.GetProperty("ingredients").EnumerateArray().Any(i => i.GetProperty("code").GetString() == "vsslssmod:lye"
                && i.GetProperty("litres").GetDouble() == .5)),
            "Plain soaps use an incorrect sealed lye process or a herbal soap recipe remains.");
        Require(ReadAsset("assets/vsslssmod/itemtypes/soap-powder.json").GetProperty("texture").GetProperty("base").GetString()
                == "vsslssmod:item/soap",
            "Soap powder does not use the mottled teal soap material.");
        var chimney = ReadAsset("assets/vsslssmod/blocktypes/chimney.json");
        var chimneyShape = ReadAsset("assets/vsslssmod/shapes/block/chimney.json");
        Require(chimney.GetProperty("shape").GetProperty("base").GetString() == "vsslssmod:block/chimney"
            && VerticalBounds("assets/vsslssmod/shapes/block/chimney.json") == (-3, 32)
            && chimney.GetProperty("selectionBoxes")[0].GetProperty("y1").GetSingle() == -3f/16
            && chimney.GetProperty("selectionBoxes")[0].GetProperty("y2").GetSingle() == 2
            && chimney.GetProperty("collisionBoxes").GetArrayLength() > 3,
            "Chimney bounds no longer match its grille, embedded middle and upper model.");
        foreach (var element in chimneyShape.GetProperty("elements").EnumerateArray())
        {
            var from = element.GetProperty("from"); var to = element.GetProperty("to");
            var uv = element.GetProperty("faces").GetProperty("south").GetProperty("uv");
            Require(Math.Abs(uv[2].GetSingle() - uv[0].GetSingle()) == to[0].GetSingle() - from[0].GetSingle()
                && Math.Abs(uv[3].GetSingle() - uv[1].GetSingle()) == to[1].GetSingle() - from[1].GetSingle(),
                "Chimney chisel faces stretch the whole texture instead of retaining voxel texel density.");
        }
        var soap = ReadAsset("assets/vsslssmod/itemtypes/soap.json");
        Require(soap.GetProperty("shape").GetProperty("base").GetString() == "game:item/food/bread/speltbread"
            && soap.GetProperty("textures").GetProperty("bread").GetProperty("base").GetString() == "vsslssmod:item/soap",
            "Soap no longer uses the vanilla bread geometry with its custom texture.");
        foreach (var item in new[] { "soap-chamomile", "soap-lavender", "soap-mint", "soap-sage", "soap-thyme" })
        {
            Require(!File.Exists(Path.Combine(root, "assets/vsslssmod/itemtypes/" + item + ".json")),
                item + " still has an item definition after herbal soap removal.");
        }
        using (var language = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/lang/zh-cn.json"))))
            Require(language.RootElement.GetProperty("item-soap").GetString() == "肥皂", "Base soap display name retained the shape description.");
        Console.WriteLine("PASS fixture blueprint model bounds and vanilla ground-storage item behavior");
        var tree = new TreeAttribute();
        tree.SetDouble("storageL", 600000); tree.SetDouble("capacityL", 900000); tree.SetFloat("fillFraction", .7f);
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (method, _) =>
            method.Name == "get_Side" ? EnumAppSide.Server
                : throw new Exception("Well local storage unexpectedly accessed the region: " + method.Name));
        var reservoir = new BlockEntityWellHead { Pos = pos, Block = head };
        try { reservoir.FromTreeAttributes(tree, world); throw new Exception("Old well accepted."); }
        catch (InvalidDataException) { }
        try { reservoir.ToTreeAttributes(new TreeAttribute()); throw new Exception("Old well overwritten."); }
        catch (InvalidDataException) { }
        reservoir = new BlockEntityWellHead { Pos = pos, Block = head };
        var saved = new TreeAttribute();
        tree.SetString("seraphFormat", "seraph.release.well"); tree.SetInt("seraphVersion", 1);
        tree.SetInt("wellReservoirSchema", 1); tree.SetDouble("storageL", 80); tree.SetDouble("capacityL", 200);
        tree.SetBool("wellComplete", true); tree.SetInt("wellDepth", 2);
        tree.SetDouble("waterTemperature", 9); tree.SetDouble("waterSalt", 1.2);
        tree.SetDouble("waterHazard", .2);
        reservoir.FromTreeAttributes(tree, world);
        var draw = typeof(BlockEntityWellHead).GetMethod("TryDraw", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require(!(bool)draw.Invoke(reservoir, [81d])! && !(bool)draw.Invoke(reservoir, [double.NaN])!, "Well overdraft accepted.");
        Require((bool)draw.Invoke(reservoir, [10d])!, "Local well water could not be drawn.");
        saved = new(); reservoir.ToTreeAttributes(saved);
        Require(saved.GetDouble("storageL") == 70 && reservoir.FillFraction > 0 && reservoir.FillFraction < .1,
            "Drawing did not reduce local storage/water height independently from the aquifer.");
        var reloaded = new BlockEntityWellHead { Pos = pos, Block = head };
        reloaded.FromTreeAttributes(saved, world);
        var again = new TreeAttribute(); reloaded.ToTreeAttributes(again);
        Require(again.GetDouble("storageL") == 70, "Reloading restored an instant full well.");
        var stored = System.Text.Json.JsonSerializer.Deserialize<Seraph.Core.Framework.WaterBatch>(again.GetString("storedWater"))!;
        Require(stored.VolumeL == 70 && stored.TemperatureC == 9 && stored.SaltGramsPerL == 1.2
            && stored.EntericHazardPerL == .2 && stored.Turbidity == .15,
            "Legacy well migration, draw or reload purified existing stored water.");
        var mixed = stored.Mix(new() { VolumeL = 30, TemperatureC = 19, Turbidity = .01,
            PathogensPerL = new Dictionary<string, double> { ["giardia"] = .4 } });
        again.SetDouble("storageL", 100); again.SetString("storedWater", System.Text.Json.JsonSerializer.Serialize(mixed));
        reloaded.FromTreeAttributes(again, world);
        var mixedSave = new TreeAttribute(); reloaded.ToTreeAttributes(mixedSave);
        var mixedReload = System.Text.Json.JsonSerializer.Deserialize<Seraph.Core.Framework.WaterBatch>(mixedSave.GetString("storedWater"))!;
        Require(mixedReload == mixed || mixedReload.VolumeL == 100 && mixedReload.TemperatureC == 12
            && Math.Abs(mixedReload.EntericHazardPerL - .14) < 1e-10
            && Math.Abs(mixedReload.PathogensPerL["giardia"] - .12) < 1e-10,
            "Stored full water batch did not conserve mixed quality across reload.");
        var replaced = new BlockEntityWellHead { Pos = pos, Block = head };
        var fresh = new TreeAttribute(); replaced.ToTreeAttributes(fresh);
        Require(fresh.GetDouble("storageL") == 0, "A replacement well head started filled.");
        Console.WriteLine("PASS release well format rejection, draw bounds, physical water height, save reload and empty replacement heads");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}


