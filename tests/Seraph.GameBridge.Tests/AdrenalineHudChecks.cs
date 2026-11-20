using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

internal static class AdrenalineHudChecks
{
    internal static void Run(Assembly mod, string? nativeGame)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var hud = mod.GetType("Seraph.Client.AdrenalineHud", true)!;
        var remaining = hud.GetMethod("Remaining", flags)!;
        var state = new ServerSnapshot { Adrenaline = 1 };
        double Time(double current, ServerSnapshot? next, ServerSnapshot? previous, double dt, bool paused = false)
            => (double)remaining.Invoke(null, [current, next, previous, dt, paused])!;
        if (Time(0, state, null, .1) != 60 || Time(60, state, state, .1) != 59.9
            || Time(10, state, state, 5, true) != 10 || Time(10, null, state, .1) != 0
            || Time(10, new() { Adrenaline = .5 }, state, .1) != 10
            || Time(0, new() { Adrenaline = double.NaN }, null, .1) != 0)
            throw new Exception("Adrenaline HUD countdown failed snapshot, pause or disconnect rules.");
        if (nativeGame is not null && OperatingSystem.IsWindows())
        {
            var display = mod.GetType("Seraph.Client.AdrenalineDisplay", true)!;
            var edges = display.GetMethod("DrawEdges", flags)!;
            var output = TestArtifacts.DirectoryPath("adrenaline-hud-render");
            Directory.CreateDirectory(output);
            var previousScale = RuntimeEnv.GUIScale;
            try
            {
                foreach (var scale in new[] { 1f, 1.5f, 2f })
                {
                    RuntimeEnv.GUIScale = scale;
                    using var surface = new ImageSurface(Format.Argb32, 1280, 720);
                    using var ctx = new Context(surface);
                    edges.Invoke(null, [ctx, 1280d, 720d]); surface.Flush();
                    var pixels = surface.Data;
                    int Alpha(int x, int y) => pixels[y * surface.Stride + x * 4 + 3];
                    if (Alpha(640, 360) != 0 || Alpha(2, 360) < 150 || Alpha(640, 2) < 150
                        || Alpha(1277, 360) < 150 || Alpha(640, 717) < 150)
                        throw new Exception("Adrenaline vignette lost a visible yellow edge or obscured the center.");
                    using var number = new ImageSurface(Format.Argb32, 192, 128);
                    using var numberContext = new Context(number);
                    display.GetMethod("DrawNumber", flags)!.Invoke(null, [numberContext, 20, false]); number.Flush();
                    if (!number.Data.Where((_, i) => i % 4 == 3).Any(a => a > 200)) throw new Exception("Countdown numeral is invisible.");
                    // Neutral background makes transparency visible in image viewers.
                    using var preview = new ImageSurface(Format.Argb32, 1280, 720);
                    using var previewContext = new Context(preview);
                    previewContext.SetSourceRGB(.12, .16, .19); previewContext.Paint();
                    previewContext.SetSourceSurface(surface, 0, 0); previewContext.Paint();
                    preview.WriteToPng($"{output}/adrenaline-scale{scale:0.0}.png");
                }
            }
            finally { RuntimeEnv.GUIScale = previousScale; }
        }
        Console.WriteLine("PASS independent adrenaline HUD: authority, real-time countdown, pause, disconnect and native Cairo yellow-edge/clear-center renders at scales 1/1.5/2");
    }

}
