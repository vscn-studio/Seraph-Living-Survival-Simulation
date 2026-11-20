using System.Reflection;
using Seraph;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class CopperBathChecks
{
    private sealed class HeatBlock : Block, IHeatSource
    {
        public bool Fail;
        public float Strength = 10;
        public float GetHeatStrength(IWorldAccessor world, BlockPos source, BlockPos receiver)
            => Fail ? throw new InvalidOperationException("Source unavailable") : Strength;
    }

    internal static void Run(string root)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var mod = new SeraphModSystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? mod : null);
        var chunk = Proxy<IWorldChunk>((_, _) => null);
        var air = new Block { Code = new("game:air") };
        var nativeFire = new BlockEntityFirepit { fuelBurnTime = 100 };
        var blockHeat = new HeatBlock();
        var anchor = new BlockPos(10, 20, 30, 0);
        BlockPos sourcePos = anchor.DownCopy();
        bool loaded = true, blockSource = false;
        var parts = new Dictionary<BlockPos, BlockEntityBathtub>();
        var accessor = Proxy<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetClimateAt" => new ClimateCondition { Temperature = 20 },
            "GetChunkAtBlockPos" => loaded ? chunk : null,
            "GetBlockEntity" => ((BlockPos)a![0]!).Equals(sourcePos) && !blockSource ? nativeFire
                : parts.GetValueOrDefault((BlockPos)a![0]!),
            "GetBlock" => blockSource && ((BlockPos)a![0]!).Equals(sourcePos) ? blockHeat : air,
            "MarkBlockDirty" or "MarkBlockEntityDirty" => null,
            _ => throw new Exception("Unexpected bath accessor: " + m.Name)
        });
        var world = Proxy<IWorldAccessor>((m, _) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Side" => EnumAppSide.Server,
            _ => throw new Exception("Unexpected bath world: " + m.Name)
        });
        var api = Proxy<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server,
            _ => throw new Exception("Unexpected bath API: " + m.Name)
        });
        var blocks = new Dictionary<string, BlockBathtub>();
        foreach (var code in new[] { "copperbathtub-part", "copperbathtub", "copperbathtub-end" })
        {
            var json = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, $"assets/vsslssmod/blocktypes/{code}.json")));
            blocks[code] = new BlockBathtub { Code = new("vsslssmod:" + code),
                CollisionBoxes = json["collisionboxes"].AsObject<Cuboidf[]>(),
                SelectionBoxes = json["selectionboxes"].AsObject<Cuboidf[]>() };
        }
        var bath = new BlockEntityBathtub { Api = api, Block = blocks["copperbathtub"], Pos = anchor };
        var waterField = typeof(BlockEntityBathtub).GetField("water", hidden)!;
        var tick = typeof(BlockEntityBathtub).GetMethod("TickWaterTreatment", hidden)!;
        var direction = typeof(BlockEntityBathtub).GetProperty("Direction", hidden)!;
        WaterBatch Water() => (WaterBatch)waterField.GetValue(bath)!;
        void Set(double litres = 300, double temperature = 20) => waterField.SetValue(bath,
            new WaterBatch { VolumeL = litres, TemperatureC = temperature, SaltGramsPerL = 3,
                ChemicalHazardPerL = .2, Turbidity = .1 });
        BlockPos Cell(int d, int n) => d switch
        { 0 => anchor.AddCopy(n, 0, 0), 1 => anchor.AddCopy(0, 0, n),
          2 => anchor.AddCopy(-n, 0, 0), _ => anchor.AddCopy(0, 0, -n) };
        for (int d = 0; d < 4; d++)
        {
            direction.SetValue(bath, d);
            parts.Clear(); parts[anchor] = bath;
            for (int n = -1; n <= 1; n++)
            {
                var code = n switch { -1 => "copperbathtub-part", 1 => "copperbathtub-end", _ => "copperbathtub" };
                var entity = n == 0 ? bath : new BlockEntityBathtub { Api = api, Block = blocks[code], Pos = Cell(d, n) };
                direction.SetValue(entity, d); parts[entity.Pos] = entity;
                var collisions = blocks[code].GetCollisionBoxes(accessor, entity.Pos);
                Require(collisions.Length > 0 && collisions.Min(b => b.Y1) == 0
                    && collisions.Max(b => b.Y2) == .625f, "Copper collision shell is floating or uses the old height.");
                Require(collisions.All(b => !(b.X1 < .5 && b.X2 > .5 && b.Z1 < .5 && b.Z2 > .5 && b.Y1 < .3 && b.Y2 > .3)),
                    "Copper collision shell blocks the bathing interior.");
                sourcePos = Cell(d, n).DownCopy(); Set(); tick.Invoke(bath, null);
                Require(Water().TemperatureC > 20.45 && Water().VolumeL == 300,
                    $"Native firepit failed to heat full bath in direction {d}, cell {n}.");
                Require(Water().SaltGramsPerL == 3 && Water().ChemicalHazardPerL == .2 && Water().Turbidity == .1,
                    "Bath heating removed non-microbial contamination.");
            }
        }
        sourcePos = anchor.DownCopy();
        Set(40); tick.Invoke(bath, null); var smallRise = Water().TemperatureC - 20;
        Set(300); tick.Invoke(bath, null);
        Require(Math.Abs(smallRise / (Water().TemperatureC - 20) - 7.5) < 1e-8,
            "Heating must account for the actual water volume.");
        for (int i = 0; i < 19; i++) tick.Invoke(bath, null);
        Require(Water().TemperatureC > 28, "A burning firepit should visibly warm a full bath over ten minutes.");
        nativeFire.fuelBurnTime = 0;
        Set(300, 40); tick.Invoke(bath, null);
        Require(Water().TemperatureC < 40, "Extinguished firepit kept heating water.");
        nativeFire.fuelBurnTime = 100;
        loaded = false; Set(); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 20, "Unloaded heat source was sampled.");
        loaded = true; sourcePos = anchor.AddCopy(3, -1, 0); Set(); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 20, "A fire outside the footprint directly heated the bath.");
        sourcePos = anchor.DownCopy(); blockSource = true; Set(); tick.Invoke(bath, null);
        Require(Water().TemperatureC > 20, "Block-level IHeatSource was ignored.");
        blockHeat.Fail = true; Set(); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 20, "Failed third-party heat source corrupted water.");
        blockHeat.Fail = false; blockHeat.Strength = float.NaN; Set(); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 20, "Invalid heat strength corrupted water.");
        blockHeat.Strength = 10; Set(40, 99.9); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 100, "Bath temperature exceeded boiling or failed to reach it.");
        var saved = new TreeAttribute(); bath.ToTreeAttributes(saved);
        var restored = new BlockEntityBathtub { Block = bath.Block, Pos = anchor };
        restored.FromTreeAttributes(saved, world);
        Require(((WaterBatch)waterField.GetValue(restored)!).TemperatureC == 100, "Heated bath lost its temperature on reload.");
        bath.Block = new BlockBathtub { Code = new("vsslssmod:bathtub") };
        Set(200); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 20, "Wooden bath gained copper conduction.");
        bath.Block = blocks["copperbathtub-part"]; Set(); tick.Invoke(bath, null);
        Require(Water().TemperatureC == 20, "A companion independently heated the shared reservoir.");
        Console.WriteLine("PASS grounded copper collision shells, native firepit heating under all cells/directions, water mass, cooling, source failures and temperature persistence");
    }
}
