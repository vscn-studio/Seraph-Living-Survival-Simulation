using System.Reflection;
using Seraph;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class WaterImmersionChecks
{
    internal static void Run()
    {
        var type = typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.WaterImmersion", true)!;
        var sample = type.GetMethod("Sample", BindingFlags.Static | BindingFlags.NonPublic)!;
        var conductance = type.GetMethod("Conductance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var air = new Block { BlockId = 0, Code = new("game:air") };
        var water = new BlockWater { BlockId = 1, Code = new("game:water-still-7"), LiquidCode = "water", LiquidLevel = 7 };
        var lava = new Block { BlockId = 2, Code = new("game:lava-still-7"), LiquidCode = "lava", LiquidLevel = 7 };
        var chunk = (IWorldChunk)RoutingProxy.Make(typeof(IWorldChunk), (_, _) => null);
        var player = new EntityPlayer();
        player.SetSelectionBox(.6f, 1.8f);
        player.Pos.SetPos(0, 64, 0);
        player.Swimming = false;
        var depth = 1;
        var loaded = true;
        Block liquid = water;
        var reads = 0;
        var blocks = (IBlockAccessor)RoutingProxy.Make(typeof(IBlockAccessor), (m, a) => m.Name switch {
            "GetChunkAtBlockPos" => loaded ? chunk : null,
            "GetBlock" => Read((BlockPos)a![0]!, (int)a[1]!),
            _ => throw new Exception("Unexpected immersion accessor call " + m.Name)
        });
        Block Read(BlockPos pos, int layer)
        {
            reads++;
            Require(loaded && layer == BlockLayersAccess.Fluid, "Immersion accessed unloaded or solid blocks.");
            return pos.Y >= 64 && pos.Y < 64 + depth ? liquid : air;
        }
        (BlockPos? Source, double Fraction) Sample() => ((BlockPos?, double))sample.Invoke(null, [blocks, player])!;
        var shallow = Sample();
        Require(shallow.Source?.Y == 64 && shallow.Fraction > .5 && shallow.Fraction < .6,
            "Wading without Swimming did not make partial water contact.");
        depth = 2;
        Require(Math.Abs(Sample().Fraction - 1) < 1e-9, "Full immersion was not detected.");
        depth = 1; water.LiquidLevel = 3;
        var lower = Sample();
        Require(lower.Fraction > 0 && lower.Fraction < shallow.Fraction, "Liquid level did not affect immersion.");
        player.Pos.Y = 64.8;
        Require(Sample().Fraction == 0, "Player above a low liquid surface still immersed.");
        player.Pos.Y = 64; liquid = lava;
        Require(Sample().Source is null && Sample().Fraction == 0, "Lava was treated as cooling water.");
        liquid = water; loaded = false;
        var before = reads;
        Require(Sample().Source is null && reads == before, "Immersion read an unloaded chunk.");
        Require((double)conductance.Invoke(null, [0d, .6])! == 0
            && (double)conductance.Invoke(null, [1d, 0d])! > (double)conductance.Invoke(null, [1d, .6])!
            && reads < 30, "Water conductance lost area/clothing effects or bounded reads.");
        Console.WriteLine("PASS actual water height: shallow wading, immersion, low liquid, lava exclusion and loaded-only bounded reads");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
