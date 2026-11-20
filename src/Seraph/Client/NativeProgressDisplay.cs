using System.Reflection;
using System.Runtime.ExceptionServices;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>Real vanilla statbars; native Cairo uploads occur only on a changed tenth, at most ten times a second.</summary>
internal sealed class NativeProgressDisplay(ICoreClientAPI api, string id, int rows, double width, double height,
    double[][] colors) : IDisposable
{
    internal string[]? TrendKeys { get; init; }
    private readonly int[] previousDirections = new int[rows];
    internal GuiComposer? Composer { get; private set; }
    private string layout = "";
    private readonly string[] previousText = new string[rows];
    private readonly float[] previousValues = Enumerable.Repeat(float.NaN, rows).ToArray();
    private double nextUpdate;
    private readonly ICoreClientAPI barApi = NativeProgressApi.Wrap(api);
    internal int Compositions { get; private set; }
    internal int ValueUpdates { get; private set; }

    internal void Update(double?[] values, string[] labels, string[] text, double scale, double left, double top, double time)
    {
        var guiScale = Math.Max(.1, RuntimeEnv.GUIScale);
        var key = string.Join("|", labels) + $":{scale:R}:{guiScale:R}:{left:R}:{top:R}";
        var rebuilt = Composer is null || key != layout;
        if (rebuilt)
        {
            Composer?.Dispose();
            var ratio = scale / guiScale;
            Composer = api.Gui.CreateCompo(id, ElementBounds.Fixed(left / guiScale, top / guiScale, width * ratio, height * ratio));
            for (var i = 0; i < rows; i++)
            {
                var y = (12 + i * 52) * ratio;
                var font = CairoFont.WhiteSmallText().WithFontSize((float)(16 * ratio));
                Composer.AddStaticText(labels[i], font, ElementBounds.Fixed(14 * ratio, y - 2 * ratio, (width - 118) * ratio, 24 * ratio));
                Composer.AddDynamicText("", font.Clone().WithOrientation(EnumTextOrientation.Right),
                    ElementBounds.Fixed((width - 108) * ratio, y - 2 * ratio, 94 * ratio, 24 * ratio), "value" + i);
                Composer.AddInteractiveElement(new GuiElementStatbar(barApi,
                    ElementBounds.Fixed(14 * ratio, y + 24 * ratio, (width - 28) * ratio, 10 * ratio),
                    colors[i], false, false), "bar" + i);
            }
            Composer.Compose();
            for (var i = 0; i < rows; i++)
            {
                var bar = Composer.GetStatbar("bar" + i);
                bar.ShowValueOnHover = false; bar.SetLineInterval(10);
                previousText[i] = ""; previousValues[i] = float.NaN;
            }
            layout = key; Compositions++;
        }
        if (!rebuilt && time < nextUpdate) return;
        nextUpdate = time + .2;
        for (var i = 0; i < rows; i++)
        {
            var amount = (float)Math.Round(Math.Clamp(values[i] ?? 0, 0, 100), 1);
            if (previousValues[i] != amount)
            {
                Composer!.GetStatbar("bar" + i).SetValues(amount, 0, 100);
                previousValues[i] = amount; ValueUpdates++;
            }
            var direction = TrendKeys is null ? 0 : MetricTrendDisplay.For(api)?.Direction(TrendKeys[i]) ?? 0;
            var displayText = text[i] + (TrendKeys is null || text[i].Length == 0 ? "" : " " + MetricTrendDisplay.Symbol(direction));
            if (previousText[i] != displayText || previousDirections[i] != direction)
            { var element = Composer!.GetDynamicText("value" + i); element.SetNewText(displayText); previousText[i] = displayText; previousDirections[i] = direction; }
        }
    }
    internal void Render(float dt = 0) => Composer?.Render(dt);
    public void Dispose() { Composer?.Dispose(); Composer = null; layout = ""; nextUpdate = 0; }
}

/// <summary>
/// Vanilla statbars queue Cairo uploads on worker threads. A close/resize may dispose their textures
/// before the queued main-thread callback runs; skip that upload rather than revive a disposed texture.
/// All elements remain unmodified GuiElementStatbar instances and all other API calls pass through.
/// </summary>
internal class NativeProgressApi : DispatchProxy
{
    private object target = null!;
    private IGuiAPI? gui;
    internal static ICoreClientAPI Wrap(ICoreClientAPI api)
    {
        var guardedGui = Create<IGuiAPI, NativeProgressApi>();
        ((NativeProgressApi)(object)guardedGui).target = api.Gui;
        var guardedApi = Create<ICoreClientAPI, NativeProgressApi>();
        var proxy = (NativeProgressApi)(object)guardedApi;
        proxy.target = api; proxy.gui = guardedGui;
        return guardedApi;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "get_Gui" && gui is not null) return gui;
        if (method.Name == "LoadOrUpdateCairoTexture" && args?[2] is LoadedTexture { Disposed: true }) return null;
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
}
