using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Seraph.World;
using Vintagestory.GameContent;

namespace Seraph.Client;

public sealed partial class SeraphClientBridge : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly Diagnostics.PerformanceClient? performance;
    private readonly SlowCallbackProbe tickProbe = new("client.status.tick");
    private readonly SlowCallbackProbe snapshotProbe = new("client.snapshot.receive");
    private readonly IClientNetworkChannel channel;
    private long requestId;
    private long worldActionSequence;
    private bool disposed;
    private long lastExhaustionWarning = long.MinValue / 2;
    internal void WarnExhaustion(string? reason = null)
    {
        var now = api.World.ElapsedMilliseconds;
        if (now - lastExhaustionWarning < 2500) return;
        reason ??= PredictedFunctions()?.ExhaustionReason;
        if (reason is not ("stamina" or "lactate" or "release" or "jump-cooldown") || !HasFreshSnapshot) return;
        lastExhaustionWarning = now;
        api.TriggerIngameError(this, "vsslssmod-exhaustion", LocalizedText.Get("vsslssmod:exertion-denied-" + reason,
            (workPrediction?.UnlockStamina ?? .20) * 100, (workPrediction?.UnlockLactateRatio ?? .6) * 100));
    }
    private bool wantsDiagnostics;
    private bool wantsPollution;
    private readonly PollutionDebugHighlights pollutionHighlights;
    private readonly long listener;
    private readonly long holdListener;
    private bool drinkPressed;
    private bool pressurePressed;
    private long lastPressureHeartbeat;
    private readonly SymptomRenderer symptoms;
    private long lastDrinkHeartbeat;
    private bool bucketWashPressed;
    private bool waterGestureLatched;
    private bool defecatePressed;
    private bool closedEyePressed;
    private long lastWashHeartbeat;
    private const string PressureKey = "vsslssmod-pressure-hold-p";
    private const string DefecateKey = "vsslssmod-defecate-shift-v";
    private const string ClosedEyeKey = "vsslssmod-closed-eye-k";
    internal MetricTrendDisplay Trends { get; }
    private readonly SurvivalHud hud;
    private readonly ActivityHud activityHud;
    private readonly AdrenalineHud adrenalineHud;
    private readonly DarkAdaptationRenderer darkAdaptation;
    internal PanelMetric? DarkAdaptationMetric => darkAdaptation?.Metric;
    private readonly CharacterPanelIntegration characterPanel;
    private readonly NativeHudReplacement nativeHud;
    private readonly NativeWaterInfoHud waterInfoHud;
    private readonly PlayerPhysicsCompatibility physicsCompatibility;
    private readonly NativeFoodTooltips foodTooltips;
    private readonly SmithingTooltipCompatibility smithingTooltips;
    private readonly GroundwaterParticleRenderer groundwaterParticles;
    private NutritionHandbookDialog? nutritionHandbook;
    internal Server.FoodCatalog? Foods { get; private set; }
    private readonly Dictionary<(int X, int Y, int Z, int D), (long At, WaterHoverSnapshot Water)> waterCache = new();
    private readonly Dictionary<(int X, int Y, int Z, int D), long> waterQueryAt = new();
    private readonly Dictionary<(int X, int Y, int Z, int D), long> waterRequestIds = new();
    private long? lastWaterQueryAt;
    private readonly Dictionary<string, long> waterLogAt = new();
    public ServerSnapshot? Snapshot { get; private set; }
    public string StatusMessage { get; private set; } = Lang.Get("vsslssmod:client-waiting");
    public long SettingsResponseSequence { get; private set; }
    public long DebugValueResponseSequence { get; private set; }
    public string DebugValueStatus { get; private set; } = "";
    private long debugValueRequestId;
    public DateTime LastReceivedUtc { get; private set; }
    private string? connectionError;
    private long helloAttempts;
    public bool HasFreshSnapshot => channel.Connected && connectionError is null && Snapshot is not null
        && DateTime.UtcNow - LastReceivedUtc <= TimeSpan.FromSeconds(5);
    public string ConnectionMessage => !channel.Connected ? Lang.Get("vsslssmod:client-channel-unavailable")
        : connectionError ?? (Snapshot is null ? Lang.Get("vsslssmod:client-handshake-pending", helloAttempts)
        : !HasFreshSnapshot ? Lang.Get("vsslssmod:client-snapshot-stale")
        : Lang.Get("vsslssmod:client-sync-ready", Snapshot.Sequence));

    // Singleplayer pause stops server snapshots. Only presentation may retain that connected state;
    // action/HUD authority continues to use the normal five-second freshness check.
    private ServerSnapshot? GetSymptomSnapshot() => HasFreshSnapshot ||
        (api.IsGamePaused && channel.Connected && connectionError is null) ? Snapshot : null;

    public SeraphClientBridge(ICoreClientAPI api)
    {
        this.api = api;
        performance = new(api, () => Snapshot);
        TooltipText.InitializeMainThread();
        Trends = new(api);
        hud = new(api, () => HasFreshSnapshot, () => darkAdaptation?.IconStage ?? 0);
        activityHud = new(api);
        nativeHud = new(api);
        waterInfoHud = new(api, WaterInfo, () => HasFreshSnapshot ? Snapshot : null);
        physicsCompatibility = new(api);
        foodTooltips = new();
        smithingTooltips = new(api);
        groundwaterParticles = new(api);
        pollutionHighlights = new(api, () => (wantsDiagnostics || wantsPollution) && HasFreshSnapshot ? Snapshot : null);
        symptoms = new(api, GetSymptomSnapshot);
        adrenalineHud = new(api, GetSymptomSnapshot);
        darkAdaptation = new(api, GetSymptomSnapshot);
        characterPanel = new(api, this);
        api.ChatCommands.Create("vsslssmodhud").WithDescription(Lang.Get("vsslssmod:hud-command"))
            .HandleWith(_ =>
            {
                hud.ToggleVisibility();
                return Vintagestory.API.Common.TextCommandResult.Success(Lang.Get("vsslssmod:hud-toggled"));
            });
        api.ChatCommands.Create("slssdebug").WithDescription(Lang.Get("vsslssmod:activity-debug-command"))
            .HandleWith(_ => Vintagestory.API.Common.TextCommandResult.Success(
                Lang.Get(activityHud.ToggleDebug() ? "vsslssmod:activity-debug-on" : "vsslssmod:activity-debug-off")));
        api.ChatCommands.Create("slsspollution").WithDescription(Lang.Get("vsslssmod:pollution-debug-command"))
            .HandleWith(_ =>
            {
                wantsPollution = !wantsPollution;
                if (!wantsPollution && !wantsDiagnostics) pollutionHighlights.Clear();
                SendHello();
                return TextCommandResult.Success(Lang.Get(wantsPollution ? "vsslssmod:pollution-debug-on" : "vsslssmod:pollution-debug-off"));
            });
        channel = api.Network.RegisterChannel(Protocol.Channel)
            .RegisterMessageType<ClientHello>().RegisterMessageType<ServerSnapshot>()
            .RegisterMessageType<SettingsRequest>().RegisterMessageType<SettingsResponse>()
            .RegisterMessageType<WorldActionRequest>().RegisterMessageType<WaterInfoRequest>()
            .RegisterMessageType<WaterInfoResponse>().RegisterMessageType<GroundwaterReveal>()
            .RegisterMessageType<FoodCatalogMessage>()
            .RegisterMessageType<DebugValueRequest>().RegisterMessageType<DebugValueResponse>()
            .RegisterMessageType<ActivityLedger>().RegisterMessageType<NativeWorkReceipt>().RegisterMessageType<WorkIntent>()
            .RegisterMessageType<OrganicMistParticle>();
        channel.SetMessageHandler<OrganicMistParticle>(packet => api.Event.EnqueueMainThreadTask(() =>
        {
            if (disposed || packet is null) return;
            var type = typeof(SeraphClientBridge).Assembly.GetType("Seraph.Server.RealSmokeGasTransport");
            type?.GetMethod("RenderMist", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(null, [api, packet]);
        }, "vsslssmod-organic-mist"));
        channel.SetMessageHandler<ActivityLedger>(packet => api.Event.EnqueueMainThreadTask(() =>
        {
            if (!disposed && HasFreshSnapshot && packet.SessionEpoch == Snapshot?.SessionEpoch)
            { ReceiveExertion(packet.Exertion); if (!packet.StateOnly) { activityHud.Update(packet); ReceivePressure(packet.Pressure); } WarnExhaustion(packet.DeniedReason); }
        }, "vsslssmod-activity"));
        channel.SetMessageHandler<DebugValueResponse>(packet => api.Event.EnqueueMainThreadTask(() =>
        {
            if (disposed || packet.SessionEpoch != Snapshot?.SessionEpoch || packet.RequestId != debugValueRequestId) return;
            DebugValueStatus = packet.Message;
            DebugValueResponseSequence++;
        }, "vsslssmod-debug-value-result"));
        channel.SetMessageHandler<WaterInfoResponse>(packet => api.Event.EnqueueMainThreadTask(
            () => ReceiveWaterInfo(packet), "vsslssmod-water-result"));
        channel.SetMessageHandler<GroundwaterReveal>(packet => api.Event.EnqueueMainThreadTask(() =>
        {
            if (disposed || packet.SessionEpoch != Snapshot?.SessionEpoch) return;
            groundwaterParticles.Reveal(packet);
        }, "vsslssmod-groundwater-particles"));
        channel.SetMessageHandler<FoodCatalogMessage>(packet => api.Event.EnqueueMainThreadTask(() =>
        {
            if (disposed || packet.SessionEpoch != Snapshot?.SessionEpoch || packet.Json is null || packet.Json.Length > 1048576) return;
            try
            {
                var catalog = System.Text.Json.JsonSerializer.Deserialize<Server.FoodCatalog>(packet.Json);
                if (catalog?.Validate() is null && catalog is not null) Foods = catalog;
            }
            catch (System.Text.Json.JsonException ex) { api.Logger.Warning("[vsslssmod] Food catalog rejected: {0}", ex.Message); }
        }, "vsslssmod-food-catalog"));
        // Fresh IDs retire the old P-drink / Ctrl+H / Ctrl+K / J bindings.
        RegisterAction("pressure", PressureKey, GlKeys.P);
        RegisterAction("defecate", DefecateKey, GlKeys.V, shift: true);
        RegisterAction("closed-eye", ClosedEyeKey, GlKeys.K);
        api.Input.InWorldAction += OnWorldInput;
        holdListener = api.Event.RegisterGameTickListener(dt => { PollExertion(); FlushMining(false); PollWaterHold(); PollPressureHold(); PollClosedEyeKey(); Trends.Tick(GetSymptomSnapshot(), HasFreshSnapshot, api.World.Player?.Entity.Alive == true, api.IsGamePaused, dt, null); }, 50);
        channel.SetMessageHandler<ServerSnapshot>(packet => api.Event.EnqueueMainThreadTask(() => Receive(packet), "vsslssmod-snapshot"));
        channel.SetMessageHandler<SettingsResponse>(packet => api.Event.EnqueueMainThreadTask(() =>
        {
            if (disposed || packet.SessionEpoch != Snapshot?.SessionEpoch || packet.RequestId != requestId) return;
            StatusMessage = packet.Message;
            SettingsResponseSequence++;
        }, "vsslssmod-settings-result"));
        api.Event.LevelFinalize += OnLevelReady;
        api.Event.LeaveWorld += OnLeaveWorld;
        api.Event.BlockChanged += WaterBlockChanged;
        listener = api.Event.RegisterGameTickListener(_ =>
        {
            using var timing = tickProbe.Measure(api);
            // Character selection can finish after LevelFinalize; retry until PlayerReady has accepted a hello.
            if (Snapshot is null || DateTime.UtcNow - LastReceivedUtc > TimeSpan.FromSeconds(3)) SendHello();
            hud.Update(Snapshot, !HasFreshSnapshot);
            if (!HasFreshSnapshot) activityHud.TryClose();
        }, 1000);
    }

    private void OnLevelReady() { OnLeaveWorld(); SendHello(); }
    private void RegisterAction(string action, string id, GlKeys key, bool shift = false)
    {
        api.Input.RegisterHotKey(id, LocalizedText.Get("vsslssmod:hotkey-" + action), key,
            HotkeyType.CharacterControls, shiftPressed: shift);
        api.Input.SetHotKeyHandler(id, _ =>
        {
            if (action == "pressure")
            {
                if (!pressurePressed && api.Input.MouseGrabbed && HasFreshSnapshot)
                { pressurePressed = true; SendAction("pressure-start"); lastPressureHeartbeat = api.World.ElapsedMilliseconds; }
                return true;
            }
            if (action == "closed-eye")
            {
                if (!api.Input.MouseGrabbed) return false;
                if (!closedEyePressed)
                {
                    closedEyePressed = true;
                    if (!disposed && HasFreshSnapshot && Snapshot is { } closedEyeSnapshot)
                        channel.SendPacket(new WorldActionRequest { SessionEpoch = closedEyeSnapshot.SessionEpoch,
                            Sequence = ++worldActionSequence, Action = "closed-eye-toggle" });
                    else api.ShowChatMessage(ConnectionMessage);
                }
                return true;
            }
            if (!api.Input.MouseGrabbed) return false;
            if (defecatePressed) return true;
            defecatePressed = true;
            if (!disposed && HasFreshSnapshot && Snapshot is { } snapshot)
                channel.SendPacket(new WorldActionRequest { SessionEpoch = snapshot.SessionEpoch,
                    Sequence = ++worldActionSequence, Action = action });
            else api.ShowChatMessage(ConnectionMessage);
            return true;
        });
    }
    private void OnLeaveWorld() { pollutionHighlights.Clear(); wantsPollution = false; nutritionHandbook?.TryClose(); nutritionHandbook?.Dispose(); nutritionHandbook = null; Trends.Reset(); ResetWorkPrediction(); pendingMining = null; nativeWorkSequence = 0; lastExhaustionWarning = long.MinValue / 2; ClearPressureDisplay(); pressureWasActive = false; pressureStatusSequence = 0; pressureActionId = 0; pressureStatusEpoch = ""; DebugValueStatus = ""; symptoms.Reset(); adrenalineHud.Reset(); darkAdaptation.Reset(); Foods = null; waterCache.Clear(); waterQueryAt.Clear(); waterRequestIds.Clear(); lastWaterQueryAt = null; waterLogAt.Clear(); waterInfoHud.Reset(); groundwaterParticles.Clear(); drinkPressed = false; bucketWashPressed = false; waterGestureLatched = false; defecatePressed = false; closedEyePressed = false; pressurePressed = false; Snapshot = null; NativeHudReplacement.Active = false; connectionError = null; helloAttempts = 0; StatusMessage = Lang.Get("vsslssmod:client-waiting"); LastReceivedUtc = default; hud.TryClose(); activityHud.ResetDebug(); }

    internal void OpenNutritionHandbook()
    {
        if (disposed || Foods is null)
        {
            api.ShowChatMessage(Lang.Get("vsslssmod:nutrition-handbook-unavailable"));
            return;
        }
        nutritionHandbook?.TryClose();
        nutritionHandbook?.Dispose();
        nutritionHandbook = new NutritionHandbookDialog(api, this);
        nutritionHandbook.TryOpen();
    }

    private void LogWaterInfo(string stage, string detail)
    {
        var now = api.World.ElapsedMilliseconds;
        if (waterLogAt.TryGetValue(stage, out var last) && now >= last && now - last < 5000) return;
        waterLogAt[stage] = now;
        api.Logger.Notification("[vsslssmod] [water-info] client {0}: {1}", stage, detail);
    }

    private void WaterBlockChanged(Vintagestory.API.MathTools.BlockPos pos, Block oldBlock)
    {
        foreach (var key in waterCache.Keys.Concat(waterQueryAt.Keys).Distinct().Where(k => k.D == pos.dimension
            && Math.Abs(k.X - pos.X) <= 8 && Math.Abs(k.Z - pos.Z) <= 8 && k.Y <= pos.Y + 1).ToArray())
        { waterCache.Remove(key); waterQueryAt.Remove(key); waterRequestIds.Remove(key); }
        waterInfoHud.RefreshSoon();
    }

    private void ReceiveWaterInfo(WaterInfoResponse packet)
    {
        if (disposed) return;
        if (packet.SessionEpoch != Snapshot?.SessionEpoch || packet.Water is not { } w)
        {
            LogWaterInfo("drop", $"epoch={packet.SessionEpoch}, expected={Snapshot?.SessionEpoch}, water={packet.Water is not null}");
            return;
        }
        var key = (w.X, w.Y, w.Z, w.Dimension);
        if (packet.RequestId > 0 && (!waterRequestIds.TryGetValue(key, out var request) || packet.RequestId != request)) return;
        if (!string.IsNullOrEmpty(packet.FailureReason))
        {
            // A transient rejection must not become a ten-second cached result.
            LogWaterInfo("reject", $"cell={w.X},{w.Y},{w.Z},d{w.Dimension}, reason={packet.FailureReason}");
            return;
        }
        LogWaterInfo("receive", $"cell={w.X},{w.Y},{w.Z},d{w.Dimension}, lines={w.Lines.Length}");
        var changed = !waterCache.TryGetValue(key, out var previous)
            || WaterSnapshotChanged(previous.Water, w);
        if (waterCache.Count >= 128 && !waterCache.ContainsKey(key)) waterCache.Remove(waterCache.MinBy(p => p.Value.At).Key);
        var receivedAt = api.World.ElapsedMilliseconds;
        performance?.WaterReceived(packet.RequestId, receivedAt, w.ClassificationPending);
        w.Formatted = string.Join("\n", w.Lines);
        waterCache[key] = (receivedAt, w);
        foreach (var neighbor in packet.Nearby.Take(4))
        {
            if (neighbor.Dimension != w.Dimension || neighbor.Y != w.Y
                || Math.Abs((long)neighbor.X - w.X) + Math.Abs((long)neighbor.Z - w.Z) != 1) continue;
            var neighborKey = (neighbor.X, neighbor.Y, neighbor.Z, neighbor.Dimension);
            // A newer direct request or observation takes precedence over prefetch.
            if (waterQueryAt.ContainsKey(neighborKey)
                || waterCache.TryGetValue(neighborKey, out var existing) && existing.At >= packet.RequestId - 1) continue;
            if (waterCache.Count >= 128 && !waterCache.ContainsKey(neighborKey)) waterCache.Remove(waterCache.MinBy(p => p.Value.At).Key);
            neighbor.Formatted = string.Join("\n", neighbor.Lines);
            waterCache[neighborKey] = (receivedAt, neighbor);
        }
        // Surface water is deliberately marked pending while its pollution
        // sample is being classified. The server can therefore return the
        // same answer several times; rebuilding the native HUD for each
        // identical packet made the tooltip appear to refresh forever.
        if (changed) waterInfoHud?.RefreshSoon();
        waterQueryAt.Remove((w.X, w.Y, w.Z, w.Dimension));
        waterRequestIds.Remove(key);
    }

    internal string WaterInfo(Vintagestory.API.MathTools.BlockPos pos)
    {
        var now = api.World.ElapsedMilliseconds;
        var key = (pos.X, pos.Y, pos.Z, pos.dimension);
        var cached = TryGetWaterCache(key, now, out var item);
        var queried = waterQueryAt.TryGetValue(key, out var sentAt);
        // Never subtract long.MinValue: that overflows and prevents the first request.
        // Pending surface samples are still useful observations. Keep them
        // visible for a normal cache interval and let a later request update
        // the text, instead of polling the server twice per second forever.
        var fresh = cached && now >= item.At && now - item.At < 10000;
        if (HasFreshSnapshot && !fresh && (!queried || now < sentAt || now - sentAt >= 500)
            && (lastWaterQueryAt is not { } last || now < last || now - last >= 200))
        {
            if (queried) LogWaterInfo("retry", $"cell={pos}, ageMs={now - sentAt}");
            if (waterQueryAt.Count >= 128 && !queried)
            {
                var oldest = waterQueryAt.MinBy(p => p.Value).Key;
                waterQueryAt.Remove(oldest); waterRequestIds.Remove(oldest);
            }
            // A slow round trip must remain valid after a retry.
            if (!waterRequestIds.ContainsKey(key)) waterRequestIds[key] = now + 1;
            lastWaterQueryAt = now;
            waterQueryAt[key] = now;
            sentAt = now;
            queried = true;
            channel.SendPacket(new WaterInfoRequest { RequestId = waterRequestIds[key], SessionEpoch = Snapshot!.SessionEpoch, X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension });
            LogWaterInfo("send", $"cell={pos}, epoch={Snapshot.SessionEpoch}");
        }
        if (cached) return item.Water.Formatted ??= string.Join("\n", item.Water.Lines);
        if (!HasFreshSnapshot) LogWaterInfo("waiting", ConnectionMessage);
        var fluid = api.World.BlockAccessor.GetBlock(pos, Vintagestory.API.Common.BlockLayersAccess.Fluid);
        var source = WaterInteractionTarget.IsWaterBlock(fluid)
            ? LocalizedText.Get("vsslssmod:water-source-" + (fluid.LiquidCode == "saltwater" ? "salt" : "surface"))
            : "";
        return LocalizedText.Get("vsslssmod:water-info", source) + "\n" +
            (HasFreshSnapshot
                ? Lang.Get("vsslssmod:water-hover-pending")
                : Lang.Get("vsslssmod:water-hover-unavailable"));
    }

    internal static bool WaterSnapshotChanged(WaterHoverSnapshot previous, WaterHoverSnapshot current)
        => previous.X != current.X || previous.Y != current.Y || previous.Z != current.Z
            || previous.Dimension != current.Dimension
            || previous.ClassificationPending != current.ClassificationPending
            || !previous.Lines.SequenceEqual(current.Lines, StringComparer.Ordinal);

    private bool TryGetWaterCache((int X, int Y, int Z, int D) key, long now,
        out (long At, WaterHoverSnapshot Water) item)
    {
        // Keep the last successful observation visible while refreshing it.
        if (waterCache.TryGetValue(key, out item) && now - item.At is >= 0 and < 120000) return true;
        item = default;
        return false;
    }

    private void SendAction(string action)
    {
        if (!disposed && HasFreshSnapshot && Snapshot is { } snapshot)
        {
            var sequence = ++worldActionSequence;
            if (action == "pressure-start") pressureActionId = sequence;
            channel.SendPacket(new WorldActionRequest { SessionEpoch = snapshot.SessionEpoch,
                Sequence = sequence, Action = action, ActionId = action.StartsWith("pressure-", StringComparison.Ordinal) ? pressureActionId : 0 });
        }
    }
    private long pressureActionId;
    private long pressureStatusSequence;
    private string pressureStatusEpoch = "";
    private bool pressureWasActive;
    private PressureStatus? acceptedPressure;
    private void ClearPressureDisplay()
    { acceptedPressure = null; if (Snapshot is not null) Snapshot.Pressure = null; hud.UpdatePressure(null); }
    private void ReceivePressure(PressureStatus? status)
    {
        if (status is null || Snapshot is null) return;
        if (pressureStatusEpoch != Snapshot.SessionEpoch)
        { ClearPressureDisplay(); pressureStatusEpoch = Snapshot.SessionEpoch; pressureStatusSequence = 0; pressureWasActive = false; }
        if (status.Sequence <= pressureStatusSequence || status.ActionId != pressureActionId) return;
        pressureStatusSequence = status.Sequence;
        if (!status.Active && pressureWasActive && status.Reason.Length > 0)
            api.TriggerIngameError(this, "vsslssmod-pressure", LocalizedText.Get("vsslssmod:pressure-" + status.Reason));
        acceptedPressure = status.Active && pressurePressed ? status : null;
        Snapshot.Pressure = acceptedPressure;
        pressureWasActive = status.Active;
        hud.UpdatePressure(Snapshot.Pressure);
    }
    private void OnWorldInput(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (action != EnumEntityAction.InWorldRightMouseDown || !on || handled != EnumHandling.PassThrough
            || !HasFreshSnapshot || !api.Input.MouseGrabbed || api.World.Player?.Entity is not { } entity
            || !entity.Controls.ShiftKey) return;
        // Claim the entire gesture until release, including after completion or cancellation.
        if (waterGestureLatched) { handled = EnumHandling.PreventDefault; return; }
        if (api.World.Player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible is BlockLiquidContainerBase) return;
        var target = WaterInteractionTarget.Find(api.World, entity);
        if (target is null) return;
        var produce = NativeCropPollution.Washable(api.World.Player.InventoryManager.ActiveHotbarSlot.Itemstack, api.World);
        var bucket = WaterInteractionTarget.IsWaterBucket(api.World, target);
        var container = WaterInteractionTarget.IsWaterContainer(api.World, target);
        if (!bucket && !WaterInteractionTarget.IsSurfaceWater(api.World, target)
            && !container) return;
        waterGestureLatched = true;
        handled = EnumHandling.PreventDefault;
        // A wooden bucket uses this gesture to wash. Larger placed vessels such
        // as an open barrel use it to drink; quiet shift posture remains the
        // washing gesture through TryAutoWash.
        bucketWashPressed = bucket && !produce; drinkPressed = !bucket || produce;
        lastWashHeartbeat = lastDrinkHeartbeat = api.World.ElapsedMilliseconds;
        SendAction(bucketWashPressed ? "clean-start" : "drink-start");
    }

    private void StopWaterGesture()
    {
        var wasActive = drinkPressed || bucketWashPressed;
        if (drinkPressed) SendAction("drink-stop");
        if (bucketWashPressed) SendAction("clean-stop");
        drinkPressed = bucketWashPressed = false;
        if (wasActive && Snapshot?.ActionProgress?.Action is "drink" or "clean" or "produce-wash") Snapshot.ActionProgress = null;
    }

    private void PollWaterHold()
    {
        if (!api.Input.IsHotKeyPressed(DefecateKey)) defecatePressed = false;
        var held = HasFreshSnapshot && api.Input.MouseGrabbed
            && api.World.Player?.Entity?.Controls.ShiftKey == true && api.Input.InWorldMouseButton.Right;
        if (!held)
        {
            var wasActive = drinkPressed || bucketWashPressed;
            StopWaterGesture(); waterGestureLatched = false;
            if (!api.Input.MouseGrabbed && Snapshot?.ActionProgress?.Action == "clean")
            { SendAction("clean-stop"); Snapshot.ActionProgress = null; wasActive = true; }
            if (wasActive) hud.Update(Snapshot, !HasFreshSnapshot);
            return;
        }
        var now = api.World.ElapsedMilliseconds;
        if (drinkPressed && now - lastDrinkHeartbeat >= 200)
        { lastDrinkHeartbeat = now; SendAction("drink-hold"); }
        if (bucketWashPressed && now - lastWashHeartbeat >= 200)
        { lastWashHeartbeat = now; SendAction("clean-hold"); }
    }

    private void PollPressureHold()
    {
        if (!pressurePressed) return;
        if (!HasFreshSnapshot || !api.Input.MouseGrabbed || !api.Input.IsHotKeyPressed(PressureKey))
        {
            SendAction("pressure-stop"); pressurePressed = false;
            ClearPressureDisplay();
            hud.Update(Snapshot, !HasFreshSnapshot); return;
        }
        if (api.World.ElapsedMilliseconds - lastPressureHeartbeat < 200) return;
        lastPressureHeartbeat = api.World.ElapsedMilliseconds; SendAction("pressure-hold");
    }

    private void PollClosedEyeKey()
    {
        if (!api.Input.IsHotKeyPressed(ClosedEyeKey)) closedEyePressed = false;
    }

    private void Receive(ServerSnapshot packet)
    {
        using var timing = snapshotProbe?.Measure(api);
        if (disposed) return;
        if (packet.ProtocolVersion != Protocol.Version)
        {
            SetConnectionError(Lang.Get("vsslssmod:client-protocol-mismatch", Protocol.Version, packet.ProtocolVersion));
            return;
        }
        // A handshake rejection is a status response, never a usable snapshot.
        if (string.IsNullOrEmpty(packet.SessionEpoch) || packet.Sequence <= 0)
        {
            SetConnectionError(string.IsNullOrWhiteSpace(packet.Summary) ? Lang.Get("vsslssmod:client-panel-missing") : packet.Summary);
            return;
        }
        if (Snapshot?.SessionEpoch == packet.SessionEpoch && packet.Sequence <= Snapshot.Sequence) return;
        if (!drinkPressed && packet.ActionProgress?.Action is "drink" or "produce-wash") packet.ActionProgress = null;
        if (!pressurePressed && packet.ActionProgress?.Action == "pressure") packet.ActionProgress = null;
        if (Snapshot is { } previous && previous.SessionEpoch != packet.SessionEpoch) { Foods = null; waterCache.Clear(); waterQueryAt.Clear(); waterRequestIds.Clear(); lastWaterQueryAt = null; }
        Snapshot = packet;
        performance?.SnapshotReceived();
        ReceiveExertion(packet.Exertion);
        ReceivePressure(packet.Pressure);
        packet.Pressure = acceptedPressure;
        LastReceivedUtc = DateTime.UtcNow;
        if (helloAttempts > 0 && (connectionError is not null || packet.Sequence == 1))
            api.Logger.Notification("[vsslssmod] Server snapshot received; protocol {0}, sequence {1}.", packet.ProtocolVersion, packet.Sequence);
        connectionError = null;
        hud.Update(packet, false);
    }

    private void SetConnectionError(string message)
    {
        if (connectionError != message) api.Logger.Warning("[vsslssmod] {0}", message);
        connectionError = message;
    }

    public void RequestDiagnostics(bool enabled)
    {
        wantsDiagnostics = enabled;
        if (!enabled && !wantsPollution) pollutionHighlights.Clear();
        SendHello();
    }

    private void SendHello()
    {
        if (!disposed && channel.Connected)
        {
            helloAttempts++;
            if (helloAttempts == 1 || helloAttempts % 30 == 0)
                api.Logger.Notification("[vsslssmod] Sending hello {0}; protocol {1}.", helloAttempts, Protocol.Version);
            channel.SendPacket(new ClientHello { RequestDiagnostics = wantsDiagnostics || wantsPollution });
        }
    }

    public void ApplySettings(double speed, bool telemetry, long revision)
    {
        var snapshot = Snapshot;
        if (disposed || !channel.Connected || snapshot is null || !snapshot.CanEditSettings
            || DateTime.UtcNow - LastReceivedUtc > TimeSpan.FromSeconds(5))
        {
            StatusMessage = Lang.Get("vsslssmod:client-admin-connection-required");
            SettingsResponseSequence++;
            return;
        }
        StatusMessage = Lang.Get("vsslssmod:client-confirmation-pending");
        channel.SendPacket(new SettingsRequest
        {
            RequestId = ++requestId, SessionEpoch = snapshot.SessionEpoch, ExpectedRevision = revision,
            PhysiologySpeed = speed, TelemetryEnabled = telemetry
        });
    }

    public void ApplyDebugValue(string field, double value, long lifeGeneration)
    {
        if (disposed || !HasFreshSnapshot || Snapshot is not { CanViewDiagnostics: true, CanEditSettings: true } snapshot
            || lifeGeneration != snapshot.LifeGeneration)
        {
            DebugValueStatus = Lang.Get("vsslssmod:client-admin-connection-required");
            DebugValueResponseSequence++;
            return;
        }
        DebugValueStatus = Lang.Get("vsslssmod:client-confirmation-pending");
        channel.SendPacket(new DebugValueRequest { SessionEpoch = snapshot.SessionEpoch,
            RequestId = ++debugValueRequestId, LifeGeneration = lifeGeneration, Field = field, Value = value });
    }

    public void Dispose()
    {
        disposed = true;
        nutritionHandbook?.TryClose();
        nutritionHandbook?.Dispose();
        nutritionHandbook = null;
        performance?.Dispose();
        api.Event.LevelFinalize -= OnLevelReady;
        api.Event.LeaveWorld -= OnLeaveWorld;
        api.Event.BlockChanged -= WaterBlockChanged;
        api.Input.InWorldAction -= OnWorldInput;
        api.Event.UnregisterGameTickListener(listener);
        api.Event.UnregisterGameTickListener(holdListener);
        hud.Dispose();
        activityHud.Dispose();
        adrenalineHud.Dispose();
        darkAdaptation.Dispose();
        nativeHud.Dispose();
        foodTooltips.Dispose();
        smithingTooltips.Dispose();
        groundwaterParticles.Dispose();
        pollutionHighlights.Dispose();
        waterInfoHud.Dispose();
        physicsCompatibility.Dispose();
        symptoms.Dispose();
        characterPanel.Dispose();
        Trends.Dispose();
        BodyRenderer.Release(api);
        foreach (var id in new[] { PressureKey, DefecateKey, ClosedEyeKey }) api.Input.SetHotKeyHandler(id, _ => false);
        Snapshot = null;
    }
}
