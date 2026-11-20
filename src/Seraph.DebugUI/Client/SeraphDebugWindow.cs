using System;
using System.Numerics;
using Seraph.Core.Simulation;
using Seraph.Network;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSImGui.API;

namespace Seraph.DebugUI.Client;

/// <summary>
/// A client projection of data that the server has authorized this player to receive.
/// Diagnostic lines must be populated only from an authorized diagnostic snapshot.
/// </summary>
public sealed record SeraphDebugView
{
    public bool HasSnapshot { get; init; }
    public bool CanViewDiagnostics { get; init; }
    public bool CanEditSettings { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string[] PhysiologyLines { get; init; } = [];
    public string[] EnvironmentLines { get; init; } = [];
    public string[] ClockLines { get; init; } = [];
    public string[] QualityLines { get; init; } = [];
    public string[] PanelStatusLines { get; init; } = [];
    public string[] PanelTemperatureLines { get; init; } = [];
    public string[] PanelNutritionLines { get; init; } = [];
    public string[] PanelRecoveryLines { get; init; } = [];
    public string[] PanelHydrationLines { get; init; } = [];
    public string[] PanelWoundLines { get; init; } = [];
    public string[] WaterInteractionLines { get; init; } = [];
    public double PhysiologySpeed { get; init; } = 1;
    public bool TelemetryEnabled { get; init; }
    public long SettingsRevision { get; init; }
    /// <summary>Incremented by the client bridge for every server settings response.</summary>
    public long SettingsResponseSequence { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public PanelMetric[] DebugValues { get; init; } = [];
    public string SessionEpoch { get; init; } = "";
    public long LifeGeneration { get; init; }
    public long DebugValueResponseSequence { get; init; }
    public string DebugValueStatus { get; init; } = "";
}

/// <summary>
/// Optional client UI. Settings remain a draft until the server acknowledges them.
/// This assembly and its ImGui dependencies must never be loaded on a dedicated server.
/// </summary>
public sealed class SeraphDebugWindow : ImGuiDialogBase
{
    private const float MinimumSpeed = (float)SchedulerOptions.MinimumPhysiologySpeed;
    private const float MaximumSpeed = (float)SchedulerOptions.MaximumPhysiologySpeed;
    private const long ResponseTimeoutMilliseconds = 15_000;

    private readonly Func<SeraphDebugView?> readView;
    private readonly Action<double, bool, long> applySettings;
    private readonly Action<string, double, long>? applyPlayerValue;
    private string editField = "bowel";
    private string editEpoch = "";
    private long editLife = -1;
    private float editValue;
    private bool editLoaded;
    private bool editPending;
    private long editResponse;
    private long editSubmittedAt;
    private readonly DebugFont font = new();
    private readonly ILogger logger;
    private bool disposed;
    private bool draftLoaded;
    private bool draftChanged;
    private float draftSpeed = 1;
    private bool draftTelemetry;
    private long draftRevision;
    private bool pending;
    private long submittedResponseSequence;
    private long submittedRevision;
    private long submittedAtMilliseconds;
    private string localStatusKey = string.Empty;

    public bool IsOpen => Opened;

    public SeraphDebugWindow(
        ICoreClientAPI api,
        Func<SeraphDebugView?> readView,
        Action<double, bool, long> applySettings,
        Action<string, double, long>? applyPlayerValue = null)
        : base(ValidateArguments(api, readView, applySettings))
    {
        logger = api.Logger;
        this.readView = readView;
        this.applySettings = applySettings;
        this.applyPlayerValue = applyPlayerValue;
    }

    public void Toggle()
    {
        if (disposed) return;
        if (Opened) Close();
        else Open();
    }

    protected override bool OnDraw()
    {
        bool pushed = font.Push(logger);
        try
        {
            using var theme = SeraphDebugTheme.Use();
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowViewport(viewport.ID);
            ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.FirstUseEver, new Vector2(0.5f));
            ImGui.SetNextWindowSize(new Vector2(Math.Min(660, viewport.Size.X - 24), Math.Min(720, viewport.Size.Y - 24)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(Math.Min(350, viewport.Size.X), Math.Min(300, viewport.Size.Y)), viewport.Size);
            bool keepOpen = true;
            bool drawContents = ImGui.Begin(Lang.Get("vsslssmoddebug:window-title") + "###vsslssmod-debug", ref keepOpen, ImGuiWindowFlags.NoDocking);
            try
            {
                // Dear ImGui permits most of a window to remain outside a resized viewport.
                // Keep the title and settings reachable after shrinking the game window.
                var size = ImGui.GetWindowSize();
                var position = ImGui.GetWindowPos();
                var clamped = Vector2.Clamp(position, viewport.Pos, viewport.Pos + Vector2.Max(Vector2.Zero, viewport.Size - size));
                if (position != clamped) ImGui.SetWindowPos(clamped, ImGuiCond.Always);
                if (drawContents) DrawContents(readView());
            }
            finally
            {
                // Dear ImGui requires End even when Begin returns false (collapsed window).
                ImGui.End();
            }

            // The installed VSImGui 1.2.7 implementation closes when OnDraw returns false.
            return keepOpen;
        }
        finally
        {
            if (pushed) ImGui.PopFont();
        }
    }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
    {
        if (!Opened || disposed) return CallbackGUIStatus.Closed;
        if (!OnDraw()) Close();
        return Opened ? CallbackGUIStatus.GrabMouse : CallbackGUIStatus.Closed;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (!disposed)
            {
                disposed = true;
                if (disposing)
                {
                    try { Close(); }
                    finally { font.Dispose(); }
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private void DrawContents(SeraphDebugView? view)
    {
        if (view is null)
        {
            ImGui.TextUnformatted(Lang.Get("vsslssmoddebug:waiting-for-snapshot"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(view.Summary)) WrappedText(view.Summary);
        if (!view.HasSnapshot) { ResetDraft(); ResetPlayerEdit(); return; }

        // Hide all diagnostic sections and settings when permission is absent,
        // even if a caller accidentally leaves data in its local projection.
        if (!view.CanViewDiagnostics)
        {
            ResetDraft();
            ResetPlayerEdit();
            ImGui.TextDisabled(Lang.Get("vsslssmoddebug:diagnostics-authorization-required"));
            return;
        }

        ImGui.Separator();
        float footerHeight = ImGui.GetFrameHeightWithSpacing() * (view.CanEditSettings ? 8 : 3);
        bool drawSnapshot = ImGui.BeginChild("##server-snapshot", new Vector2(0, -footerHeight), false);
        try
        {
            if (drawSnapshot)
            {
                DrawPlayerEdit(view);
                DrawLines(Lang.Get("vsslssmoddebug:section-physiology") + "###section-physiology", view.PhysiologyLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-environment") + "###section-environment", view.EnvironmentLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-clocks") + "###section-clocks", view.ClockLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-data-quality") + "###section-data-quality", view.QualityLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-player-panel") + "###section-player-panel", view.PanelStatusLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-player-temperature") + "###section-player-temperature", view.PanelTemperatureLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-player-nutrition") + "###section-player-nutrition", view.PanelNutritionLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-player-recovery") + "###section-player-recovery", view.PanelRecoveryLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-player-hydration") + "###section-player-hydration", view.PanelHydrationLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-player-wounds") + "###section-player-wounds", view.PanelWoundLines);
                DrawLines(Lang.Get("vsslssmoddebug:section-water-interactions") + "###section-water-interactions", view.WaterInteractionLines);
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGui.Separator();
        if (view.CanEditSettings)
        {
            ObserveSettings(view);
            DrawSettings(view);
        }
        else
        {
            ResetDraft();
            ImGui.TextDisabled(Lang.Get("vsslssmoddebug:settings-authorization-required"));
        }

        if (!string.IsNullOrWhiteSpace(view.StatusMessage)) WrappedText(view.StatusMessage);
        if (!string.IsNullOrWhiteSpace(localStatusKey)) WrappedText(Lang.Get(localStatusKey));
    }

    private void ResetPlayerEdit()
    {
        editLoaded = editPending = false;
        editEpoch = ""; editLife = -1;
    }

    private void DrawPlayerEdit(SeraphDebugView view)
    {
        if (!view.CanEditSettings || applyPlayerValue is null || view.DebugValues.Length == 0)
        { ResetPlayerEdit(); return; }
        if (editEpoch != view.SessionEpoch || editLife != view.LifeGeneration)
        {
            ResetPlayerEdit(); editEpoch = view.SessionEpoch; editLife = view.LifeGeneration;
        }
        if (editPending && view.DebugValueResponseSequence > editResponse)
        { editPending = false; editLoaded = false; }
        if (editPending && Environment.TickCount64 - editSubmittedAt >= ResponseTimeoutMilliseconds)
            editPending = false;
        if (!ImGui.CollapsingHeader(Lang.Get("vsslssmoddebug:section-edit-player") + "###edit-player", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var selected = Array.Find(view.DebugValues, v => v.Key == editField) ?? view.DebugValues[0];
        if (!editLoaded) { editField = selected.Key; editValue = (float)selected.Value; editLoaded = true; }
        ImGui.BeginDisabled(editPending);
        try
        {
            ImGui.SetNextItemWidth(Math.Max(100, ImGui.GetContentRegionAvail().X * .6f));
            if (ImGui.BeginCombo(Lang.Get("vsslssmoddebug:edit-field"), Lang.Get("vsslssmoddebug:edit-" + editField)))
            {
                try
                {
                    foreach (var value in view.DebugValues)
                        if (ImGui.Selectable(Lang.Get("vsslssmoddebug:edit-" + value.Key), value.Key == editField))
                        { selected = value; editField = value.Key; editValue = (float)value.Value; }
                }
                finally { ImGui.EndCombo(); }
            }
            WrappedText(Lang.Get("vsslssmoddebug:edit-current", selected.Value.ToString("0.##"), selected.Unit,
                selected.Minimum.ToString("0.##"), selected.Maximum.ToString("0.##")));
            ImGui.SetNextItemWidth(Math.Max(100, ImGui.GetContentRegionAvail().X * .6f));
            ImGui.InputFloat(Lang.Get("vsslssmoddebug:edit-draft"), ref editValue, 1, 10, "%.2f");
            if (ImGui.Button(Lang.Get("vsslssmoddebug:reload-confirmed-values") + "##player")) editValue = (float)selected.Value;
            if (editField == "bowel")
            {
                ImGui.SameLine();
                if (ImGui.Button("150 g")) editValue = 150;
                ImGui.SameLine();
                if (ImGui.Button("400 g")) editValue = 400;
            }
            var valid = float.IsFinite(editValue) && editValue >= selected.Minimum && editValue <= selected.Maximum;
            ImGui.BeginDisabled(!valid);
            try
            {
                if (ImGui.Button(Lang.Get("vsslssmoddebug:edit-apply")))
                {
                    editPending = true; editResponse = view.DebugValueResponseSequence; editSubmittedAt = Environment.TickCount64;
                    try { applyPlayerValue(editField, editValue, editLife); }
                    catch (Exception ex) { editPending = false; logger.Warning("[vsslssmoddebug] Player edit send failed: {0}", ex); }
                }
            }
            finally { ImGui.EndDisabled(); }
        }
        finally { ImGui.EndDisabled(); }
        if (editPending) WrappedText(Lang.Get("vsslssmoddebug:waiting-for-validation"));
        else if (!string.IsNullOrEmpty(view.DebugValueStatus)) WrappedText(view.DebugValueStatus);
        ImGui.Separator();
    }

    private static void DrawLines(string title, string[] lines)
    {
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.Indent();
        try
        {
            if (lines is null || lines.Length == 0)
            {
                ImGui.TextDisabled(Lang.Get("vsslssmoddebug:no-server-data"));
                return;
            }
            foreach (string line in lines)
            {
                if (line is not null) WrappedText(line);
            }
            ImGui.Spacing();
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    private static void WrappedText(string text)
    {
        ImGui.PushTextWrapPos(0);
        try { ImGui.TextUnformatted(text); }
        finally { ImGui.PopTextWrapPos(); }
    }

    private void ObserveSettings(SeraphDebugView view)
    {
        if (!draftLoaded)
        {
            LoadDraft(view);
            return;
        }

        if (pending)
        {
            bool receivedResponse = view.SettingsResponseSequence != submittedResponseSequence;
            bool revisionChanged = view.SettingsRevision != submittedRevision;
            if (receivedResponse || revisionChanged)
            {
                pending = false;
                localStatusKey = string.Empty;
                if (revisionChanged || DraftMatches(view)) LoadDraft(view);
            }
            else if (Environment.TickCount64 - submittedAtMilliseconds >= ResponseTimeoutMilliseconds)
            {
                pending = false;
                localStatusKey = "vsslssmoddebug:settings-timeout";
            }
        }

        if (!pending && !draftChanged && draftRevision != view.SettingsRevision) LoadDraft(view);
    }

    private void DrawSettings(SeraphDebugView view)
    {
        ImGui.TextUnformatted(Lang.Get("vsslssmoddebug:section-settings"));
        ImGui.TextUnformatted(Lang.Get("vsslssmoddebug:settings-confirmed", view.PhysiologySpeed,
            Lang.Get(view.TelemetryEnabled ? "vsslssmoddebug:state-on" : "vsslssmoddebug:state-off"), view.SettingsRevision));

        ImGui.BeginDisabled(pending);
        try
        {
            ImGui.TextUnformatted(Lang.Get("vsslssmoddebug:calendar-speed-hint"));
            bool changed = ImGui.Checkbox(Lang.Get("vsslssmoddebug:field-telemetry-draft") + "###telemetry-draft", ref draftTelemetry);
            if (changed)
            {
                draftChanged = !DraftMatches(view);
                localStatusKey = string.Empty;
            }

            bool staleDraft = draftRevision != view.SettingsRevision;
            if (staleDraft)
            {
                ImGui.TextUnformatted(Lang.Get("vsslssmoddebug:settings-changed"));
            }

            ImGui.BeginDisabled(!draftChanged || staleDraft || !ValidDraft());
            try
            {
                if (ImGui.Button(Lang.Get("vsslssmoddebug:apply-to-server") + "###apply-to-server")) Submit(view);
            }
            finally
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("vsslssmoddebug:reload-confirmed-values") + "###reload-confirmed-values")) LoadDraft(view);
        }
        finally
        {
            ImGui.EndDisabled();
        }

        if (pending) ImGui.TextUnformatted(Lang.Get("vsslssmoddebug:waiting-for-validation"));
    }

    private void Submit(SeraphDebugView view)
    {
        if (pending || !ValidDraft() || !view.CanEditSettings || draftRevision != view.SettingsRevision) return;

        pending = true;
        submittedAtMilliseconds = Environment.TickCount64;
        submittedResponseSequence = view.SettingsResponseSequence;
        submittedRevision = draftRevision;
        localStatusKey = string.Empty;
        try
        {
            applySettings(draftSpeed, draftTelemetry, draftRevision);
        }
        catch
        {
            pending = false;
            localStatusKey = "vsslssmoddebug:settings-send-failed";
        }
    }

    private void LoadDraft(SeraphDebugView view)
    {
        draftSpeed = double.IsFinite(view.PhysiologySpeed)
            ? (float)Math.Clamp(view.PhysiologySpeed, MinimumSpeed, MaximumSpeed)
            : 1;
        draftTelemetry = view.TelemetryEnabled;
        draftRevision = view.SettingsRevision;
        draftLoaded = true;
        draftChanged = false;
        localStatusKey = string.Empty;
    }

    private bool DraftMatches(SeraphDebugView view) =>
        Math.Abs(draftSpeed - view.PhysiologySpeed) < 0.00001 && draftTelemetry == view.TelemetryEnabled;

    private bool ValidDraft() => float.IsFinite(draftSpeed) && draftSpeed >= MinimumSpeed && draftSpeed <= MaximumSpeed;

    private static void Field(string label)
    {
        float labelWidth = ImGui.GetContentRegionAvail().X * 0.43f;
        float column = ImGui.GetCursorPosX() + labelWidth;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (ImGui.CalcTextSize(label).X + 12 <= labelWidth) ImGui.SameLine(column);
        ImGui.SetNextItemWidth(-1);
    }

    private void ResetDraft()
    {
        draftLoaded = false;
        draftChanged = false;
        pending = false;
        localStatusKey = string.Empty;
    }

    private static ICoreClientAPI ValidateArguments(
        ICoreClientAPI api,
        Func<SeraphDebugView?> readView,
        Action<double, bool, long> applySettings)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(applySettings);
        return api;
    }
}

internal static class SeraphDebugTheme
{
    internal static Scope Use()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 5));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(5, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 12);
        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14);
        (ImGuiCol Color, uint Value)[] colors =
        [
            (ImGuiCol.Text, 0xFFFFFFFF), (ImGuiCol.TextDisabled, 0xFFAAAAAA),
            (ImGuiCol.WindowBg, 0xFF282020), (ImGuiCol.ChildBg, 0), (ImGuiCol.PopupBg, 0xFF382828),
            (ImGuiCol.Border, 0xFF483C3C), (ImGuiCol.Separator, 0xFF483C3C),
            (ImGuiCol.TitleBg, 0xFF1B1515), (ImGuiCol.TitleBgActive, 0xFF261D1D), (ImGuiCol.TitleBgCollapsed, 0xFF1B1515),
            (ImGuiCol.FrameBg, 0xAA3F3838), (ImGuiCol.FrameBgHovered, 0xCC4B4343), (ImGuiCol.FrameBgActive, 0xFF322C2C),
            (ImGuiCol.Button, 0xFF524444), (ImGuiCol.ButtonHovered, 0xFFF37C00), (ImGuiCol.ButtonActive, 0xFFA25300),
            (ImGuiCol.Header, 0), (ImGuiCol.HeaderHovered, 0xBBDD7200), (ImGuiCol.HeaderActive, 0xFF954C00),
            (ImGuiCol.CheckMark, 0xFFFB8800), (ImGuiCol.SliderGrab, 0xFFFB8800), (ImGuiCol.SliderGrabActive, 0xFFFFAB45),
            (ImGuiCol.ScrollbarBg, 0xFF282020), (ImGuiCol.ScrollbarGrab, 0xFF605454),
            (ImGuiCol.ScrollbarGrabHovered, 0xFF807070), (ImGuiCol.ScrollbarGrabActive, 0xFFFB8800),
            (ImGuiCol.TextSelectedBg, 0x887F4600), (ImGuiCol.NavHighlight, 0xFFFB8800)
        ];
        foreach (var color in colors) ImGui.PushStyleColor(color.Color, color.Value);
        return new Scope(colors.Length);
    }

    internal readonly struct Scope(int colors) : IDisposable
    {
        public void Dispose()
        {
            ImGui.PopStyleColor(colors);
            ImGui.PopStyleVar(8);
        }
    }
}
