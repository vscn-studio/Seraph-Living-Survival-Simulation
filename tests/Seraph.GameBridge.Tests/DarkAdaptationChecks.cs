using System.Reflection;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Seraph.Client;

internal static class DarkAdaptationChecks
{
    private sealed class Viewer : EntityPlayer
    {
        internal readonly DummySlot Right = new(), Left = new();
        internal byte[]? Glow;
        public override byte[] LightHsv => Glow!;
        public override ItemSlot RightHandItemSlot => Right;
        public override ItemSlot LeftHandItemSlot => Left;
    }

    private sealed class Lamp : Item
    {
        internal byte Brightness = 20;
        public override byte[] GetLightHsv(IBlockAccessor access, BlockPos pos, ItemStack stack) => [0, 0, Brightness];
    }

    internal static void Run()
    {
        var eyes = new DarkAdaptationState();
        void Advance(double light, int ticks) { for (var i = 0; i < ticks; i++) eyes.Advance(light, .25, false); }
        void Near(double expected, double actual) { if (Math.Abs(expected - actual) > 1e-8) throw new Exception($"Dark adaptation expected {expected}, got {actual}."); }
        Advance(32, 240); Near(0, eyes.Level); Near(0, eyes.Strength(0));
        Advance(0, 80); Near(1d / 3, eyes.Level);
        if (eyes.Strength(0) is <= 0 or >= .15) throw new Exception("Early silhouettes are absent or too bright.");
        Advance(0, 160); Near(1, eyes.Level); Near(1, eyes.Strength(0)); Near(.25, eyes.Strength(1));
        eyes.Advance(32, 10, true); Near(1, eyes.Level);
        eyes.Advance(double.NaN, .25, false); Near(1, eyes.Level);
        Advance(32, 20); Near(0, eyes.Level); Near(0, eyes.Strength(0));
        Advance(10, 240); Near(.5, eyes.Level); Near(.5, eyes.Darkness);
        eyes.Reset(); Near(0, eyes.Level); Near(0, eyes.Strength(0));
        eyes.Advance(0, 3600, false);
        if (eyes.Level > .01) throw new Exception("A stalled frame granted offline dark adaptation.");
        eyes.Reset(); Advance(0, 167);
        if (eyes.IconStage != 0) throw new Exception("Eye icon appeared below 70%.");
        Advance(0, 1); if (eyes.IconStage != 1) throw new Exception("Gray eye did not appear at 70%.");
        Advance(0, 23); if (eyes.IconStage != 1) throw new Exception("Eye opened below 80%.");
        Advance(0, 1); if (eyes.IconStage != 2) throw new Exception("Eye did not open at 80%.");
        Advance(0, 48); Advance(32, 1);
        if (eyes.IconStage != 0) throw new Exception("Bright light retained active dark vision icon.");
        Integration();
        Console.WriteLine("PASS dark adaptation: 20/60-second darkness, 5-second bright decay, intermediate light, pause, vitamin A, reset and bounded stalls");
    }
    private static void Integration()
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        var entity = new Viewer { Alive = true };
        entity.Pos.SetPos(-2.2, 10, 3.8); entity.Pos.Dimension = 2;
        var player = FixtureClientPlayer.Make(entity);
        var light = 0; var samples = 0; var paused = false;
        var accessor = Proxy<IBlockAccessor>((m, a) => {
            if (m.Name != "GetLightLevel") throw new Exception(m.Name);
            var pos = (BlockPos)a![0]!;
            Require(pos.dimension == 2 && pos.X == -3 && (EnumLightLevelType)a[1]! == EnumLightLevelType.MaxTimeOfDayLight,
                "Eye light lost its dimension, negative coordinate or current skylight mode.");
            samples++; return light;
        });
        var world = Proxy<IClientWorldAccessor>((m, _) => m.Name switch {
            "get_Player" => player, "get_BlockAccessor" => accessor, _ => throw new Exception(m.Name)
        });
        System.Action<float>? tick = null;
        var registered = false; var unregistered = false;
        var events = Proxy<IClientEventAPI>((m, a) => {
            if (m.Name == "RegisterGameTickListener") {
                Require((int)a![1]! == 250, "Eye sampling frequency changed.");
                tick = (System.Action<float>)a[0]!; return 1L;
            }
            if (m.Name == "RegisterRenderer") { Require((EnumRenderStage)a![1]! == EnumRenderStage.AfterBlit, "Dark vision includes GUI."); registered = true; return null; }
            if (m.Name == "UnregisterRenderer") { unregistered = true; return null; }
            if (m.Name is "UnregisterGameTickListener" or "add_ReloadShader" or "remove_ReloadShader") return null;
            throw new Exception(m.Name);
        });
        var api = Proxy<ICoreClientAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Event" => events, "get_IsGamePaused" => paused, _ => throw new Exception(m.Name)
        });
        ServerSnapshot? state = new() { SessionEpoch = "eyes", LifeGeneration = 1 };
        using (var renderer = new DarkAdaptationRenderer(api, () => state))
        {
            void Advance(int n) { for (var i = 0; i < n; i++) tick!(.25f); }
            Require(registered && tick is not null, "Dark vision listeners missing.");
            Advance(240); Require(renderer.Metric.Value == 100 && renderer.IconStage == 2, "Actual eye sampler did not adapt.");
            paused = true; light = 32; Advance(20); Require(renderer.Metric.Value == 100, "Pause decayed adaptation."); paused = false;
            var lamp = new Lamp(); entity.Right.Itemstack = new ItemStack(lamp); light = 0;
            Advance(20); Require(renderer.Metric.Value == 0 && renderer.IconStage == 0, "Right held light was ignored.");
            entity.Right.Itemstack = null; Advance(240);
            entity.Left.Itemstack = new ItemStack(lamp); Advance(20); Require(renderer.Metric.Value == 0, "Left held light was ignored.");
            entity.Left.Itemstack = null; Advance(240); entity.Glow = [0, 0, 20]; Advance(20);
            Require(renderer.Metric.Value == 0, "Dynamic entity light was ignored.");
            entity.Glow = null; Advance(80); var before = renderer.Metric.Value;
            state.IsClosedEyeResting = true; var count = samples; Advance(240);
            Require(renderer.Metric.Value == before && samples == count, "Closed eyes accumulated light/adaptation.");
            Require(renderer.IconStage == 0, "Closed eyes retained active eye icon.");
            state.IsClosedEyeResting = false; state.IsSleeping = true; Advance(240);
            Require(renderer.Metric.Value == before, "Sleep accumulated adaptation.");
            state.IsSleeping = false; state.LifeGeneration = 2; Advance(1);
            Require(renderer.Metric.Value == 0, "New life retained eye adaptation.");
            Advance(240); state = null; Advance(1); Require(renderer.Metric.Value == 0, "Stale/disconnected state retained adaptation.");
            renderer.OnRenderFrame(.016f, EnumRenderStage.AfterBlit); // No render API/GL access for unavailable state.
        }
        Require(unregistered, "Dark vision renderer leaked on dispose.");
        Console.WriteLine("PASS eye sampler integration: 4 Hz/current skylight, coordinates/dimension, both held lights, pause, closed eyes/sleep, life/stale reset, world stage and cleanup");
    }
}
