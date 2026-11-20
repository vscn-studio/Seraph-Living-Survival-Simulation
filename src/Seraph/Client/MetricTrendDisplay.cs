using System.Runtime.CompilerServices;
using Cairo;
using HarmonyLib;
using Seraph.Application;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Seraph.Client;

/// <summary>Neutral direction markers beside unchanged vanilla statbars; no per-frame Cairo.</summary>
internal sealed class MetricTrendDisplay : IDisposable
{
    private sealed record Binding(MetricTrendDisplay Owner, string Key);
    private static readonly ConditionalWeakTable<GuiElementStatbar, Binding> Bindings = new();
    private static readonly ConditionalWeakTable<ICoreClientAPI, MetricTrendDisplay> Owners = new();
    private static readonly Harmony Patch = new("vsslssmod.metric-trends");
    private static int instances;
    private readonly ICoreClientAPI api;
    internal MetricTrends History { get; } = new();
    private readonly Dictionary<int, LoadedTexture> markers = [];
    private readonly Vec4f neutral = new(1, 1, 1, 1);
    private string epoch = "";
    private long life;
    private double nextSample;
    private bool disposed;
    internal static MetricTrendDisplay? For(ICoreClientAPI api) => Owners.TryGetValue(api, out var owner) ? owner : null;
    internal MetricTrendDisplay(ICoreClientAPI api)
    {
        this.api = api; Owners.Add(api, this);
        if (instances++ == 0) Patch.Patch(AccessTools.Method(typeof(GuiElementStatbar), nameof(GuiElementStatbar.RenderInteractiveElements)),
            postfix: new HarmonyMethod(typeof(MetricTrendDisplay), nameof(AfterBar)));
    }
    internal void Bind(GuiElementStatbar bar, string key)
    { Bindings.Remove(bar); Bindings.Add(bar, new(this, key)); }
    internal void Tick(ServerSnapshot? state, bool fresh, bool alive, bool paused, double seconds, double? stamina)
    {
        if (!alive || state is null || (!fresh && !paused)) { Reset(); return; }
        if (epoch != state.SessionEpoch || life != state.LifeGeneration)
        { Reset(); epoch = state.SessionEpoch; life = state.LifeGeneration; }
        if (paused) return;
        History.Advance(seconds);
        if (History.Time < nextSample) return;
        nextSample = History.Time + .2;
        foreach (var m in state.StatusMetrics.Concat(state.TemperatureMetrics).Concat(state.RecoveryMetrics).Concat(state.FunctionMetrics).DistinctBy(m => m.Key))
            History.Record(m.Key, m.Available ? m.Key == "stamina" && stamina.HasValue ? stamina.Value * 100 : m.Value : double.NaN, m.Maximum > m.Minimum ? m.Minimum : 0, m.Maximum > m.Minimum ? m.Maximum : 1);
        foreach (var g in state.Gauges)
            History.Record("hud-" + g.Key, g.Available ? g.Key == "stamina" && stamina.HasValue ? stamina.Value : g.Value : double.NaN, 0, g.Maximum);
    }
    internal void Reset() { History.Clear(); epoch = ""; nextSample = 0; }
    internal int Direction(string key) => History.Get(key, .05).Direction;
    internal static string Symbol(int direction) => direction > 0 ? "▲" : direction < 0 ? "▼" : "—";
    private static void AfterBar(GuiElementStatbar __instance)
    { if (Bindings.TryGetValue(__instance, out var binding) && !binding.Owner.disposed) binding.Owner.Draw(__instance, binding); }
    private void Draw(GuiElementStatbar bar, Binding binding)
    {
        var direction = History.Get(binding.Key).Direction;
        var bounds = bar.Bounds;
        if (!markers.TryGetValue(direction, out var texture))
        {
            texture = new(api);
            using var surface = new ImageSurface(Format.Argb32, 24, 24);
            using var ctx = new Context(surface);
            ctx.SetSourceRGBA(1, 1, 1, 1);
            if (direction == 0) ctx.Rectangle(3, 10, 18, 3);
            else
            {
                ctx.MoveTo(12, direction > 0 ? 4 : 20);
                ctx.LineTo(3, direction > 0 ? 20 : 4);
                ctx.LineTo(21, direction > 0 ? 20 : 4);
                ctx.ClosePath();
            }
            ctx.Fill();
            api.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
            markers.Add(direction, texture);
        }
        var size = 10 * RuntimeEnv.GUIScale;
        api.Render.RenderTexture(texture.TextureId, bounds.renderX + bounds.InnerWidth + 4 * RuntimeEnv.GUIScale,
            bounds.renderY + (bounds.InnerHeight - size) / 2, size, size, 51, neutral);
    }
    public void Dispose()
    { if (disposed) return; disposed = true; foreach (var texture in markers.Values) texture.Dispose(); markers.Clear(); Owners.Remove(api); Reset(); if (--instances == 0) Patch.UnpatchAll(Patch.Id); }
}
