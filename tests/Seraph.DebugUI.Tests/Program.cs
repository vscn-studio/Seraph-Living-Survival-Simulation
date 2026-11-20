using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Seraph.DebugUI.Client;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSImGui;
using VSImGui.API;
using ErrorCode = OpenTK.Graphics.OpenGL4.ErrorCode;

// This harness renders the actual UI assembly with the installed native ImGui library.
// The game API is a projection stub; this is not a connected Vintage Story client test.
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native UI checks require the local Windows renderer.");
var root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var imguiPath = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(root, "../vsimgui_1.2.7"));
var outputDirectory = Path.Combine(Path.GetTempPath(), "Seraph-tests", "ui-checks");
Directory.CreateDirectory(outputDirectory);
var logger = Stub.For<ILogger>((method, _) => Default(method.ReturnType));
var translationRoot = Path.Combine(outputDirectory, "translations");
Directory.CreateDirectory(Path.Combine(translationRoot, "game", "lang"));
foreach (var locale in new[] { "en", "zh-cn" })
{
    var entries = new Dictionary<string, string>();
    foreach (var domain in new[] { "vsslssmod", "vsslssmoddebug" })
        foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "assets", domain, "lang", locale + ".json")))!)
            entries.Add(domain + ":" + entry.Key, entry.Value);
    File.WriteAllText(Path.Combine(translationRoot, "game", "lang", locale + ".json"), JsonSerializer.Serialize(entries));
    var service = new TranslationService(locale, logger);
    service.PreLoad(translationRoot);
    Lang.AvailableLanguages[locale] = service;
}
Lang.ChangeLanguage("en");
NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, (name, _, _) =>
    name.Contains("cimgui", StringComparison.OrdinalIgnoreCase)
        ? NativeLibrary.Load(Path.Combine(imguiPath, "native/win/cimgui.dll")) : IntPtr.Zero);

using var nativeWindow = new NativeWindow(new NativeWindowSettings
{
    StartVisible = false, StartFocused = false, ClientSize = new(1280, 800),
    API = ContextAPI.OpenGL, APIVersion = new Version(3, 3), Profile = ContextProfile.Core,
    Title = "vsslssmod UI checks"
});
nativeWindow.MakeCurrent();
nativeWindow.ClientLocation = new(0, 0);
GL.LoadBindings(new GLFWBindingsContext());
OpenTK.Graphics.OpenGL.GL.LoadBindings(new GLFWBindingsContext());
var context = ImGui.CreateContext();
unsafe { ImGui.GetIO().NativePtr->IniFilename = null; }
var io = ImGui.GetIO();
io.DisplaySize = new(1280, 800);
io.DeltaTime = 1f / 60;
io.Fonts.AddFontDefault();
var renderer = new ImGuiController_OpenTK.ImGuiRenderer(ImGui.GetMainViewport(), nativeWindow);
var system = new ImGuiModSystem();
var loader = Stub.For<IModLoader>((method, _) => method.Name == "GetModSystem" ? system : Default(method.ReturnType));
var api = Stub.For<ICoreClientAPI>((method, _) => method.Name == "get_ModLoader" ? loader : method.Name == "get_Logger" ? logger : Default(method.ReturnType));
SeraphDebugView? currentView = null;
var writes = 0;
var playerWrites = 0;
using var debugWindow = new SeraphDebugWindow(api, () => currentView, (_, _, _) => writes++, (_, _, _) => playerWrites++);
var draw = typeof(SeraphDebugWindow).GetMethod("Draw", BindingFlags.NonPublic | BindingFlags.Instance)!;
var passed = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine("PASS " + message);
}

string Frame(string name, bool capture = false, bool collapsed = false, bool resetLayout = false)
{
    ImGui.NewFrame();
    ImGui.SetWindowCollapsed("vsslssmod - server state###vsslssmod-debug", collapsed, ImGuiCond.Always);
    if (resetLayout)
    {
        ImGui.SetWindowPos("vsslssmod - server state###vsslssmod-debug", new(310, 40), ImGuiCond.Always);
        ImGui.SetWindowSize("vsslssmod - server state###vsslssmod-debug", new(660, 720), ImGuiCond.Always);
    }
    var logPath = Path.Combine(outputDirectory, name + ".txt");
    File.WriteAllText(logPath, "");
    ImGui.LogToFile(0, logPath);
    var status = (CallbackGUIStatus)draw.Invoke(debugWindow, [1f / 60])!;
    ImGui.LogFinish();
    var text = File.ReadAllText(logPath);
    ImGui.Render();
    Require(ImGui.GetDrawData().Valid, name + ": ImGui stacks remain balanced");
    Require(status == (debugWindow.IsOpen ? CallbackGUIStatus.GrabMouse : CallbackGUIStatus.Closed), name + ": mouse capture follows open state");
    var width = (int)io.DisplaySize.X;
    var height = (int)io.DisplaySize.Y;
    GL.Viewport(0, 0, width, height);
    GL.ClearColor(0.12f, 0.14f, 0.15f, 1);
    GL.Clear(ClearBufferMask.ColorBufferBit);
    renderer.Render();
    Require(GL.GetError() == ErrorCode.NoError, name + ": OpenGL render succeeds");
    if (capture)
    {
        byte[] pixels = new byte[width * height * 4];
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var flipped = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(flipped)) { canvas.Translate(0, height); canvas.Scale(1, -1); canvas.DrawBitmap(bitmap, 0, 0); }
        using var output = File.Create(Path.Combine(outputDirectory, name + ".png"));
        flipped.Encode(output, SKEncodedImageFormat.Png, 100);
    }
    return text;
}

try
{
    debugWindow.Toggle();
    Frame("warmup");
    Require(Frame("waiting").Contains("Waiting for a server snapshot."), "missing snapshot displays a waiting message");
    currentView = new()
    {
        HasSnapshot = true,
        Summary = "vsslssmod shadow: observation only; vanilla owns gameplay.",
        PhysiologyLines = ["PRIVATE-SENTINEL"], EnvironmentLines = ["PRIVATE-SENTINEL"],
        ClockLines = ["PRIVATE-SENTINEL"], QualityLines = ["PRIVATE-SENTINEL"],
        CanViewDiagnostics = false, CanEditSettings = true
    };
    var denied = Frame("unauthorized", capture: true);
    Require(!denied.Contains("PRIVATE-SENTINEL") && !denied.Contains("Apply to server"), "unauthorized projection hides all diagnostics and settings even with inconsistent input");
    currentView = new()
    {
        HasSnapshot = true,
        Summary = "vsslssmod shadow: observation only; vanilla owns gameplay. Offline frozen.",
        CanViewDiagnostics = true, CanEditSettings = true,
        PhysiologyLines = ["Core: 37.000 C | skin: 33.000 C", "Water: 42.000 L | gut: 0.000 L", "Sleep pressure: 0.300"],
        EnvironmentLines = ["Air: 18.00 C | RH estimate: 50% | wind estimate: 1.00 m/s"],
        ClockLines = ["Bio elapsed: 123.00 s | speed: 1.00 | dropped: 0.000 s"],
        QualityLines = ["RH and wind are estimates. No gameplay effects."],
        PhysiologySpeed = 1, SettingsRevision = 2, StatusMessage = "Connected."
    };
    var admin = Frame("administrator", capture: true);
    Require(admin.Contains("37.000") && admin.Contains("Apply to server"), "administrator projection renders diagnostics and parameter controls");
    currentView = currentView with { SessionEpoch = "test-world", LifeGeneration = 1,
        DebugValues = [new() { Key = "bowel", Value = 150, Maximum = 400, Unit = "g", Available = true },
            new() { Key = "dirtiness", Value = 80, Maximum = 100, Unit = "%", Available = true }] };
    var editor = Frame("player-editor", capture: true);
    Require(editor.Contains("Edit own player values") && editor.Contains("Bowel contents") && editor.Contains("150")
        && editor.Contains("Save player value"), "administrator editor renders server values, bounds and save action");
    currentView = currentView with { HasSnapshot = false };
    Require(!Frame("editor-stale").Contains("Save player value"), "stale connection hides player editing controls");
    currentView = currentView with { HasSnapshot = true };
    Frame("collapsed", collapsed: true);
    currentView = currentView with { CanViewDiagnostics = false, CanEditSettings = false };
    var revoked = Frame("revoked");
    Require(!revoked.Contains("37.000") && !revoked.Contains("Apply to server"), "permission revocation hides previous diagnostic values");
    currentView = currentView with { CanViewDiagnostics = true, PhysiologyLines = Enumerable.Range(0, 300).Select(i => $"Probe row {i}: 1.000 game units").ToArray() };
    Frame("many-lines");
    io.DisplaySize = new(480, 360);
    nativeWindow.ClientSize = new(480, 360);
    currentView = currentView with { CanEditSettings = true };
    Frame("resize-warmup");
    Frame("small-viewport", capture: true);
    var geometry = Regex.Match(ImGui.SaveIniSettingsToMemory(), @"\[Window\]\[[^\r\n]*###vsslssmod-debug\]\r?\nPos=(-?\d+),(-?\d+)\r?\nSize=(\d+),(\d+)");
    Require(geometry.Success, "resized window geometry can be inspected");
    var x = int.Parse(geometry.Groups[1].Value);
    var y = int.Parse(geometry.Groups[2].Value);
    var windowWidth = int.Parse(geometry.Groups[3].Value);
    var windowHeight = int.Parse(geometry.Groups[4].Value);
    Require(x >= 0 && y >= 0 && x + windowWidth <= 480 && y + windowHeight <= 360,
        "shrinking the viewport keeps the title and settings window inside the visible area");
    Lang.ChangeLanguage("zh-cn");
    io.DisplaySize = new(1280, 800);
    nativeWindow.ClientSize = new(1280, 800);
    currentView = currentView with
    {
        Summary = "vsslssmod 影子模拟：仅观测。离线冻结。",
        PhysiologyLines = ["核心体温：37.000 °C | 皮肤温度：33.000 °C", "体水：42.000 L | 肠道水：0.000 L"],
        EnvironmentLines = ["气温：18.00 °C | 相对湿度估计：50% | 风速估计：1.00 m/s"],
        ClockLines = ["生理累计时间：123.00 s | 速度：1.00 倍"],
        QualityLines = ["气温取自原版采样；湿度、风速换算、辐射温度和衣物保温仍为估计。原版衣物保暖值不等于 clo，缓存热源强度不等于热功率；这些诊断尚未参与物理输入。"],
        StatusMessage = "已连接。"
    };
    Frame("chinese-warmup", resetLayout: true);
    var chinese = Frame("chinese", capture: true);
    Require(chinese.Contains("应用到服务端") && chinese.Contains("37.000") && !chinese.Contains("vsslssmoddebug:"),
        "Chinese resources render without untranslated UI keys");
    var fontOwner = typeof(SeraphDebugWindow).GetField("font", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(debugWindow)!;
    Require((bool)fontOwner.GetType().GetProperty("Available", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fontOwner)!,
        "installed CJK font builds and uploads its independent atlas");
    io.DisplaySize = new(480, 360);
    nativeWindow.ClientSize = new(480, 360);
    Frame("chinese-resize-warmup");
    Frame("chinese-small-viewport", capture: true);
    debugWindow.Toggle();
    Frame("closed");
    debugWindow.Dispose();
    debugWindow.Dispose();
    debugWindow.Toggle();
    Require(!debugWindow.IsOpen && writes == 0 && playerWrites == 0, "closing and repeated disposal prevent reopening and unsolicited parameter requests");
    Console.WriteLine($"Native UI checks: {passed} passed. Artifacts: {outputDirectory}");
}
finally
{
    debugWindow.Dispose();
    renderer.Dispose();
    ImGui.DestroyContext(context);
}

static object? Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

public class Stub : DispatchProxy
{
    public System.Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T For<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }
}
