using System.Text;
using System.Text.Json;
using Cairo;
using HarmonyLib;
using Seraph.Core.Framework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.World;

public sealed class PollutionMapLayer : MapLayer
{
    private sealed record MapSample(PollutionSnapshot Snapshot, double AgeSeconds, double HazardPerL, double ServingL)
    {
        public WaterBatch? Water { get; init; }
    }
    private sealed record View(IServerPlayer Player, int X1, int Z1, int X2, int Z2);
    private readonly Dictionary<string, View> viewers = new();
    private Dictionary<PollutionChunk, MapSample> cells = new();
    private LoadedTexture? pixel;
    private long listener, receivedAt;
    private long nextDiagnostic;
    private bool disposed;
    private Harmony? hoverHarmony;
    private (PollutionChunk Chunk, long Age, string Locale)? hoverKey;
    private string hoverText = "";
    private long lastHoverRefreshAt = -1;
    private GuiElementMap? pointerMap;
    private (int X, int Y, double X1, double Z1, double X2, double Z2,
        double Left, double Top, double Width, double Height, long Second)? pointerView;
    public override string Title => Lang.Get("vsslssmod:pollution-map-title");
    public override string LayerGroupCode => "vsslssmodpollution";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Server;
    public override bool RequireChunkLoaded => false;

    public PollutionMapLayer(ICoreAPI api, IWorldMapManager sink) : base(api, sink) { ZIndex = 2; }

    public override void OnLoaded()
    {
        if (api is ICoreServerAPI server) listener = server.Event.RegisterGameTickListener(_ => SendAll(), 5000);
        if (api is ICoreClientAPI)
        {
            hoverHarmony = new Harmony("vsslssmod.pollution-map-hover");
            hoverHarmony.Patch(AccessTools.Method(typeof(GuiDialogWorldMap), nameof(GuiDialogWorldMap.OnMouseMove)),
                postfix: new HarmonyMethod(typeof(PollutionMapLayer), nameof(AfterWorldMapMouseMove)));
        }
    }

    public override void OnMapOpenedClient() { pointerView = null; lastHoverRefreshAt = -1; }
    public override void OnMapClosedClient() { pointerMap = null; pointerView = null; hoverKey = null; lastHoverRefreshAt = -1; }
    public override void OnTick(float dt)
    {
        // Native hover owns tooltip visibility. Refresh text when panning, zooming or
        // receiving new data beneath a stationary pointer.
        if (!Active || api is not ICoreClientAPI client || mapSink is not WorldMapManager { IsOpened: true } manager) return;
        var dialog = manager.worldMapDlg;
        if (!dialog.Focused || dialog.DialogType != EnumDialogType.Dialog
            || dialog.SingleComposer?.GetElement("mapElem") is not GuiElementMap map
            || !ValidView(map) || !map.Bounds.PointInside(client.Input.MouseX, client.Input.MouseY))
        { pointerView = null; lastHoverRefreshAt = -1; return; }
        // Resize/recomposition can replace the native hover with an empty element
        // while the pointer and world bounds remain unchanged.
        if (!ReferenceEquals(pointerMap, map)) { pointerMap = map; pointerView = null; lastHoverRefreshAt = -1; }
        var b = map.CurrentBlockViewBounds;
        var now = client.World.ElapsedMilliseconds;
        var current = (client.Input.MouseX, client.Input.MouseY, b.X1, b.Z1, b.X2, b.Z2,
            map.Bounds.renderX, map.Bounds.renderY, map.Bounds.InnerWidth, map.Bounds.InnerHeight, now / 1000);
        if (pointerView == current) return;
        // The manager ticks every 20 ms; native SetNewText recomposes Cairo. Bound
        // synthetic refreshes while panning instead of adding one on every tick.
        if (lastHoverRefreshAt >= 0 && now >= lastHoverRefreshAt && now - lastHoverRefreshAt < 200) return;
        lastHoverRefreshAt = now;
        pointerView = current;
        dialog.OnMouseMove(new MouseEvent(current.Item1, current.Item2));
    }

    private static void AfterWorldMapMouseMove(GuiDialogWorldMap __instance, MouseEvent args)
    {
        var composer = __instance.SingleComposer;
        if (!__instance.Focused || __instance.DialogType != EnumDialogType.Dialog
            || composer?.GetElement("mapElem") is not GuiElementMap map
            || !map.Bounds.PointInside(args.X, args.Y)
            || composer.GetHoverText("hoverText") is not { } tooltip
            || map.mapLayers?.OfType<PollutionMapLayer>().FirstOrDefault(layer => layer.Active) is not { } layer)
            return;

        var text = tooltip.GetText();
        if (text.Contains(layer.Title, StringComparison.Ordinal)) return;
        var details = new StringBuilder();
        layer.OnMouseMoveClient(args, map, details);
        if (details.Length == 0) return;
        tooltip.SetNewText(string.IsNullOrWhiteSpace(text)
            ? details.ToString().TrimEnd()
            : text.TrimEnd() + Environment.NewLine + details.ToString().TrimEnd());
    }

    public override void OnMapOpenedServer(IServerPlayer player) => Queue(() =>
    {
        var pos = player.Entity.Pos.AsBlockPos;
        viewers[player.PlayerUID] = new(player, (pos.X >> 5) - 16, (pos.Z >> 5) - 16, (pos.X >> 5) + 16, (pos.Z >> 5) + 16);
        Send(viewers[player.PlayerUID]);
    });

    public override void OnMapClosedServer(IServerPlayer player) => Queue(() => viewers.Remove(player.PlayerUID));

    public override void OnViewChangedServer(IServerPlayer player, int x1, int z1, int x2, int z2) => Queue(() =>
    {
        viewers[player.PlayerUID] = new(player, Math.Min(x1, x2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(z1, z2));
        Send(viewers[player.PlayerUID]);
    });

    private void Queue(Action action) => api.Event.EnqueueMainThreadTask(() => { if (!disposed) action(); }, "vsslssmod-pollution-map");

    private void SendAll()
    {
        foreach (var view in viewers.Values.ToArray())
        {
            if (view.Player.ConnectionState != EnumClientState.Playing) { viewers.Remove(view.Player.PlayerUID); continue; }
            Send(view);
        }
    }

    private void Send(View view)
    {
        var store = api.ModLoader.GetModSystem<SeraphModSystem>().Server?.Pollution;
        if (store is null || view.Player.Entity is null) return;
        var dimension = view.Player.Entity.Pos.Dimension;
        var centerX = ((long)view.X1 + view.X2) / 2d;
        var centerZ = ((long)view.Z1 + view.Z2) / 2d;
        var data = store.Snapshots.Where(s => s.Chunk.Dimension == dimension && s.Chunk.X >= view.X1 && s.Chunk.X <= view.X2
                && s.Chunk.Z >= view.Z1 && s.Chunk.Z <= view.Z2)
            .OrderBy(s => Math.Abs(s.Chunk.X - centerX) + Math.Abs(s.Chunk.Z - centerZ)).Take(1024)
            .Select(s => store.Report(new BlockPos(s.Chunk.X * 32 + 16, 0, s.Chunk.Z * 32 + 16, s.Chunk.Dimension))!)
            .Select(s => new MapSample(s, Math.Max(0, (api.World.ElapsedMilliseconds - s.At) / 1000d),
                Math.Clamp(PollutionModel.WaterHazard(s.Load, s.TemperatureC, false, s.SaltWater > 0,
                    store.Config.FreshWaterHazardPerL, store.Config.FecalWaterHazardPerL)
                    + s.Region.SurfacePathogens * store.Config.FecalWaterHazardPerL, 0, 100), store.Config.DrinkServingL)
                    { Water = store.MapWater(s.Chunk) }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        while (bytes.Length > 1024 * 1024 && data.Length > 0)
        { data = data.Take(data.Length / 2).ToArray(); bytes = JsonSerializer.SerializeToUtf8Bytes(data); }
        mapSink.SendMapDataToClient(this, view.Player, bytes);
        Diagnose("sent", data.Length);
    }

    private void Diagnose(string stage, int count)
    {
        var now = api.World.ElapsedMilliseconds;
        if (now < nextDiagnostic) return;
        nextDiagnostic = now + 30000;
        api.Logger.Debug("[vsslssmod] Pollution map {0}: {1} sampled columns.", stage, count);
    }

    public override void OnDataFromServer(byte[] data)
    {
        if (data.Length > 1024 * 1024) return;
        try
        {
            var samples = JsonSerializer.Deserialize<MapSample[]>(data);
            if (samples is null || samples.Length > 1024 || samples.Any(s => s is null || s.Snapshot is null
                || s.Snapshot.Animals is null || s.Snapshot.Animals.Count > 128 || !double.IsFinite(s.HazardPerL)
                || s.Snapshot.Region is null || !s.Snapshot.Region.Valid() || s.Snapshot.Sources is null
                || s.HazardPerL is < 0 or > 100 || !double.IsFinite(s.ServingL) || s.ServingL is < .01 or > 1
                || !double.IsFinite(s.AgeSeconds) || s.AgeSeconds < 0 || s.Water is { } water && water.Validate() is not null))
            { Diagnose("rejected invalid payload", samples?.Length ?? 0); return; }
            var next = samples.ToDictionary(s => s.Snapshot.Chunk);
            Queue(() =>
            {
                cells = next; receivedAt = api.World.ElapsedMilliseconds; hoverKey = null; pointerView = null; lastHoverRefreshAt = -1;
                Diagnose("received", cells.Count);
                // The native map only rebuilds hover text on mouse movement. A sample
                // arriving under a stationary pointer must update that text too.
                OnTick(0);
            });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { api.Logger.Warning("[vsslssmod] Invalid pollution map update: {0}", ex.Message); }
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (!Active || api is not ICoreClientAPI client || !ValidView(map)) return;
        EnsurePixel(client);
        var dimension = client.World.Player.Entity.Pos.Dimension;
        foreach (var entry in cells.Values)
        {
            var sample = entry.Snapshot;
            if (sample.Chunk.Dimension != dimension) continue;
            var p = new Vec2f(); var q = new Vec2f();
            map.TranslateWorldPosToViewPos(new(sample.Chunk.X * 32, 0, sample.Chunk.Z * 32), ref p);
            map.TranslateWorldPosToViewPos(new(sample.Chunk.X * 32 + 32, 0, sample.Chunk.Z * 32 + 32), ref q);
            if (q.X < 0 || q.Y < 0 || p.X > map.Bounds.InnerWidth || p.Y > map.Bounds.InnerHeight) continue;
            var levels = PollutionText.Levels(sample);
            var x = (float)map.Bounds.renderX + p.X; var y = (float)map.Bounds.renderY + p.Y;
            var width = Math.Max(1, q.X - p.X); var height = Math.Max(1, q.Y - p.Y);
            var level = Math.Max(levels.Water, levels.Air);
            client.Render.Render2DTexture(pixel!.TextureId, x, y, width, height, 52,
                new Vec4f((float)(.15 + level * .75), (float)(.65 - level * .45), .35f, .26f));
        }
        // Draw only sampled cells and their shared borders. An empty or unknown
        // part of the viewport must remain clear instead of showing a world-wide
        // grid that implies data exists there.
        var color = new Vec4f(.75f, .8f, .85f, .38f);
        foreach (var edge in GridEdges(cells.Keys.Where(chunk => chunk.Dimension == dimension).Select(chunk => (chunk.X, chunk.Z))))
        {
            var p = new Vec2f(); var q = new Vec2f();
            map.TranslateWorldPosToViewPos(new(edge.X * 32, 0, edge.Z * 32), ref p);
            map.TranslateWorldPosToViewPos(new(edge.X * 32 + (edge.Vertical ? 0 : 32), 0,
                edge.Z * 32 + (edge.Vertical ? 32 : 0)), ref q);
            if (Math.Max(p.X, q.X) < 0 || Math.Max(p.Y, q.Y) < 0
                || Math.Min(p.X, q.X) > map.Bounds.InnerWidth || Math.Min(p.Y, q.Y) > map.Bounds.InnerHeight) continue;
            var x = (float)Math.Round(map.Bounds.renderX + p.X, 3);
            var y = (float)Math.Round(map.Bounds.renderY + p.Y, 3);
            var width = Math.Max(1, (float)Math.Round(Math.Abs(q.X - p.X), 3));
            var height = Math.Max(1, (float)Math.Round(Math.Abs(q.Y - p.Y), 3));
            client.Render.Render2DTexture(pixel!.TextureId, x, y, edge.Vertical ? 1 : width, edge.Vertical ? height : 1, 53, color);
        }
    }

    private static bool ValidView(GuiElementMap map) => map.Bounds.InnerWidth > 0 && map.Bounds.InnerHeight > 0
        && double.IsFinite(map.CurrentBlockViewBounds.X1) && double.IsFinite(map.CurrentBlockViewBounds.X2)
        && double.IsFinite(map.CurrentBlockViewBounds.Z1) && double.IsFinite(map.CurrentBlockViewBounds.Z2)
        && map.CurrentBlockViewBounds.Width > 0 && map.CurrentBlockViewBounds.Length > 0;

    internal static IEnumerable<float> GridLines(double first, double last, double size)
    {
        if (!double.IsFinite(first) || !double.IsFinite(last) || last <= first || size <= 0) yield break;
        var start = Math.Ceiling(first / 32); var end = Math.Floor(last / 32);
        if (end - start > 4096) yield break; // malformed/extreme view; never loop over world coordinates.
        for (var i = 0; i <= end - start; i++) yield return (float)(((start + i) * 32 - first) / (last - first) * size);
    }

    internal static IEnumerable<(int X, int Z, bool Vertical)> GridEdges(IEnumerable<(int X, int Z)> chunks)
    {
        var seen = new HashSet<(int X, int Z, bool Vertical)>();
        foreach (var (x, z) in chunks)
        {
            var edges = new[]
            {
                (x, z, true), (x + 1, z, true),
                (x, z, false), (x, z + 1, false)
            };
            foreach (var edge in edges)
                if (seen.Add(edge)) yield return edge;
        }
    }

    private void EnsurePixel(ICoreClientAPI client)
    {
        if (pixel is not null) return;
        using var surface = new ImageSurface(Format.Argb32, 1, 1);
        using var context = new Context(surface);
        context.SetSourceRGBA(1, 1, 1, 1); context.Paint();
        pixel = new LoadedTexture(client, client.Gui.LoadCairoTexture(surface, false), 1, 1);
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hover)
    {
        if (!Active || api is not ICoreClientAPI client || !ValidView(map)) return;
        // Match the coordinate conversion used by native map layers such as
        // ProspectTogether. WorldMap passes screen coordinates; TranslateViewPos
        // expects coordinates relative to the rendered map rectangle.
        var viewX = (float)(args.X - map.Bounds.renderX);
        var viewZ = (float)(args.Y - map.Bounds.renderY);
        if (viewX < 0 || viewZ < 0 || viewX > map.Bounds.OuterWidth || viewZ > map.Bounds.OuterHeight) return;
        // The hover only needs X/Z. Reproduce the native linear projection
        // without asking the map for a rain height or block accessor.
        var bounds = map.CurrentBlockViewBounds;
        var worldPos = new Vec3d(
            bounds.X1 + viewX * (bounds.X2 - bounds.X1) / map.Bounds.InnerWidth,
            0,
            bounds.Z1 + viewZ * (bounds.Z2 - bounds.Z1) / map.Bounds.InnerHeight);
        var key = new PollutionChunk((int)Math.Floor(worldPos.X / 32), (int)Math.Floor(worldPos.Z / 32),
            client.World.Player.Entity.Pos.Dimension);
        var age = cells.TryGetValue(key, out var found)
            ? (long)(found.AgeSeconds + Math.Max(0, (api.World.ElapsedMilliseconds - receivedAt) / 1000d)) : -1;
        var cacheKey = (key, age, Lang.CurrentLocale);
        if (hoverKey == cacheKey) { hover.Append(hoverText); return; }
        var text = new StringBuilder();
        BuildHover(key, age, text);
        hoverKey = cacheKey; hoverText = text.ToString(); hover.Append(hoverText);
    }

    private void BuildHover(PollutionChunk key, long age, StringBuilder hover)
    {
        hover.AppendLine(Title);
        if (!cells.TryGetValue(key, out var entry))
        {
            hover.AppendLine(Lang.Get("vsslssmod:pollution-map-unsampled"));
            return;
        }
        var sample = entry.Snapshot;
        var levels = PollutionText.Levels(sample);
        string Text(string code, object[] values) => LocalizedText.Get("vsslssmod:" + code, values);
        hover.AppendLine(Text("pollution-map-column", [key.X, key.Z]));
        if (entry.Water is { } water)
            foreach (var line in WaterInfoText.Lines(water, Text, entry.ServingL, includeVolume: false)) hover.AppendLine(line);
        else hover.AppendLine(Text(sample.FreshWater > 0 || sample.SaltWater > 0 ? "pollution-pending" : "pollution-no-surface-water", []));
        hover.AppendLine(Text("pollution-map-air", [levels.Smoke.ToString("P0"), levels.Air.ToString("P0")]));
        hover.AppendLine(Text("pollution-sources", [PollutionText.Sources(sample)]));
        hover.AppendLine(Text("pollution-hydrology", [sample.Region.GroundwaterStress.ToString("P0"), sample.Region.RechargeMultiplier.ToString("P0")]));
        hover.AppendLine(Text("pollution-map-age", [age]));
    }

    public override void Dispose()
    {
        disposed = true;
        hoverHarmony?.UnpatchAll("vsslssmod.pollution-map-hover");
        hoverHarmony = null;
        if (api is ICoreServerAPI server && listener != 0) server.Event.UnregisterGameTickListener(listener);
        pixel?.Dispose(); pixel = null; viewers.Clear(); cells.Clear();
        pointerMap = null;
    }
}
