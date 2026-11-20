using System.Globalization;
using Seraph.Application;
using Seraph.Core.Simulation;
using Seraph.Core.State;
using Seraph.Core.Framework;
using Seraph.World;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;

namespace Seraph.Server;

internal enum ClosedEyeRestMode
{
    None,
    Seated,
    Reclined
}

internal sealed partial class ServerCoordinator : IDisposable
{
    private const string ConfigFile = "vsslssmod.Server.json";
    private const string StateKey = "vsslssmod:state";
    private const string BackupKey = "vsslssmod:lastgood";
    private readonly ICoreServerAPI api;
    private readonly Dictionary<string, Session> sessions = new();
    private readonly Dictionary<GroundwaterRevealKey, double> groundwaterRevealCooldowns = new();
    private readonly InboundGate inbound = new();
    // Native work can produce ten mining batches plus four attack cycles per second.
    private readonly InboundGate workInbound = new(20, 15);
    private SettingsAuthority settings = new(new());
    private SurfaceWaterClassifier? waterClassifier;
    private GroundwaterWorldStore? groundwater;
    internal ChunkPollutionStore? Pollution { get; private set; }
    private ProspectingCompatibility? prospecting;
    private IServerNetworkChannel channel = null!;
    private SessionFaultKind? startupFault;
    private long listener;
    private bool paused;
    private bool skipNextDelta;
    private bool disposed;
    private double lastCalendarHours = double.NaN;
    private double calendarDelta;

    private readonly record struct GroundwaterRevealKey(string PlayerUid, int X, int Y, int Z, int Dimension);

    // Adapter-owned classifications keep raw Core/Application errors out of player-facing text.
    private enum SessionFaultKind
    {
        ConfigurationRejected,
        SaveFormatUnsupported,
        SaveInvalid,
        UnexpectedSessionError,
        TimeStepRejected,
        PhysiologyStepRejected
    }

    private sealed class Session(IServerPlayer player)
    {
        public IServerPlayer Player { get; } = player;
        public string Epoch { get; } = Guid.NewGuid().ToString("N");
        public BodyProfile Profile { get; set; } = BodyProfile.Default;
        public BodyState State { get; set; } = BodyState.Create();
        public FrameworkState Framework { get; set; } = FrameworkState.Create(player.PlayerUID);
        public InteractionState Interactions { get; set; } = new();
        public long LastWorldActionSequence { get; set; }
        public double LastWorldActionSeconds { get; set; } = double.NegativeInfinity;
        public HeldPressure? Pressure { get; set; }
        public double LastPressureStart { get; set; } = double.NegativeInfinity;
        public long PressureSequence { get; set; }
        public long PressureActionId { get; set; }
        public string PressureReason { get; set; } = "";
        public PendingWash? Wash { get; set; }
        public bool WaterWashLatched { get; set; }
        public ClinicalWarningTracker ClinicalWarnings { get; } = new();
        public int PendingInsectBites { get; set; }
        public double SmokeSampleElapsed = .25;
        public bool SmokeSampleValid;
        public double SmokeConcentration;
        public double SmokeDamageThisTick, ClinicalDamageThisTick;
        public PendingDefecation? Defecation { get; set; }
        public EntityAgent? GroundSquattingEntity { get; set; }
        public PendingLatrineDefecation? LatrineDefecation { get; set; }
        public PendingGroundDrink? GroundDrink { get; set; }
        public PendingProduceWash? ProduceWash { get; set; }
        public PendingWellBucketRaise? WellBucketRaise { get; set; }
        public PhysiologyObserver? Observer { get; set; }
        public Vintagestory.API.Common.Entities.Entity? ObservedEntity { get; set; }
        public EnumDamageType? LastDamageKind { get; set; }
        public string LastHitPosition { get; set; } = "";
        public string LastResolvedRegion { get; set; } = "general";
        public string LastHitQuality { get; set; } = "unavailable";
        public double LastDamageAmount { get; set; }
        public string LastCombatStages { get; set; } = "";
        public int LastHurtCounter { get; set; }
        public double LastDrinkHoldSeconds { get; set; } = double.NegativeInfinity;
        public double LastDrinkSeconds { get; set; } = double.NegativeInfinity;
        public double LastTelemetrySeconds { get; set; } = double.NegativeInfinity;
        public double LastEffectSeconds { get; set; } = double.NegativeInfinity;
        // Native action hooks arrive between physiology ticks. Keep a short,
        // server-owned pulse so the next biological step accounts for the work.
        public double ActionPowerW { get; set; }
        // ActivityCompatibility owns active mount/vehicle activity for this tick.
        // This is separate from discrete action pulses to avoid double charging.
        public double ActivityPowerW { get; set; }
        public ExertionKind HandActivity { get; set; }
        public ActionEnergyBudget ActionEnergy { get; set; } = new();
        public HeldWorkLedger HeldWork { get; set; } = new();
        public NativeWorkReceipts NativeWork { get; set; } = new();
        public WorkAcknowledgements WorkAcknowledgements { get; set; } = new();
        public int ExertionLife { get; set; }
        public double JumpCooldownRemaining { get; set; }
        public long ExertionSequence { get; set; }
        public long IntentSequence { get; set; }
        public bool IntentHeld { get; set; }
        public double IntentAt { get; set; } = double.NegativeInfinity;
        public BlockPos? LastMiningTarget { get; set; }
        public double LastMiningReceipt { get; set; }
        public Dictionary<BlockPos, (int Id, double At)> RecentBrokenBlocks { get; } = [];
        public double LastAttackCharge { get; set; } = double.NegativeInfinity;
        public double DemandPowerW { get; set; }
        public double VomitAnimationRemaining { get; set; }
        public double VomitParticleRemaining { get; set; }
        // Spoilage contact/eating events are edge-triggered. Keep the last
        // accepted event per food identity so rapid native callbacks cannot
        // repeatedly raise dirtiness.
        public Dictionary<string, double> SpoilageEvents { get; } = new(StringComparer.Ordinal);
        public double MiningUntil { get; set; }
        public double StrongUntil { get; set; }
        public WorkIntervals WorkIntervals { get; set; } = new();
        public RecoveryClock RecoveryClock { get; set; } = new();
        public WorkIntervals CalendarWorkIntervals { get; set; } = new();
        public double LastDiscreteWorkAt { get; set; } = double.NegativeInfinity;
        public string DeniedReason { get; set; } = "";
        public double RestFraction { get; set; } = 1;
        public RunningPace RunningPace { get; set; } = new();
        public ExertionKind MovementActivity { get; set; }
        public bool SprintExhausted {
            get => Framework.ExertionExhausted;
            set { if (Framework.ExertionExhausted != value) Framework = Framework with { ExertionExhausted = value }; }
        }
        // Native physics and interaction gates query several times per frame.
        // Immutable state identity makes this cache invalidate immediately on a real mutation.
        public FrameworkState? FunctionFramework { get; set; }
        public BodyState? FunctionBody { get; set; }
        public BodyProfile? FunctionProfile { get; set; }
        public (float Alcohol, float Psychedelic, double Bowel) FunctionInputs { get; set; }
        public BodyFunctions? FunctionValues { get; set; }
        public (BodyFunctions Functions, bool Movement, bool Manipulation, bool Exhausted, string Reason, bool Oxygen, bool JumpAllowed, double PaceGain)? ActionFunctionInputs { get; set; }
        public FunctionSnapshot? ActionFunctionValues { get; set; }
        public ClosedEyeRestMode ClosedEyeRest { get; set; }
        public SleepEyeCycle SleepEyes { get; } = new();
        public double SleepEyesUpdatedAt { get; set; } = double.NaN;
        public int SleepDangerMask { get; set; }
        public CalendarSleepTimeline SleepTimeline { get; } = new();
        public Entity? ClosedEyePoseEntity { get; set; }
        public bool LedgerInitialized { get; set; }
        public double LastLedgerFatigue { get; set; }
        public double PaidEnergyJ { get; set; }
        public double SettledSeconds { get; set; }
        // Activity HUD rates are sampled over a complete wall-clock window. The
        // physiology flow window can be shorter than one server callback, which
        // made the old display divide by tiny, changing samples while idle.
        public double ActivityWindowWallSeconds { get; set; }
        public double ActivityWindowBioSeconds { get; set; }
        public double ActivityWindowPaidEnergyJ { get; set; }
        public double ActivityWindowStaminaDelta { get; set; }
        public double ActivityWindowOxygenDelta { get; set; }
        public double ActivityWindowFatigueDelta { get; set; }
        public double ActivityRateEnergy { get; set; }
        public double ActivityRateStamina { get; set; }
        public double ActivityRateOxygen { get; set; }
        public double ActivityRateFatigue { get; set; }
        public double EnergyBasalKcalPerGameHour { get; set; }
        public double EnergyColdKcalPerGameHour { get; set; }
        public double EnergyActivityKcalPerGameHour { get; set; }
        public double EnergyRecoveryKcalPerGameHour { get; set; }
        public double EnergyTotalKcalPerGameHour { get; set; }
        public bool HasEnergySnapshot { get; set; }
        public ThermalLedger Thermal { get; set; } = new();
        public PhysiologyFlowLedger Flows { get; set; } = new();
        public bool ResetFlowWindow { get; set; } = true;
        public bool PhysiologyAdvancing { get; set; }
        public double LastLedgerStamina { get; set; } = 1;
        public double LastLedgerOxygen { get; set; } = 1;
        public ExertionKind Activity { get; set; }
        public double ActionUntilSeconds { get; set; } = double.NegativeInfinity;
        public bool Sheltered { get; set; }
        public bool Teleported { get; set; }
        public double LastX { get; set; }
        public double LastY { get; set; }
        public double LastZ { get; set; }
        public bool PositionKnown { get; set; }
        public SimulationKernel Kernel { get; set; } = new(BodyProfile.Default);
        public CalendarTimeScheduler Scheduler { get; set; } = new();
        public EnvironmentSample? Environment { get; set; }
        public SanitationContext Sanitation { get; set; } = new();
        public double LastSampleSeconds { get; set; } = double.NegativeInfinity;
        public double LastGoodSampleSeconds { get; set; } = double.NegativeInfinity;
        public string SampleQuality { get; set; } = L(player, "server-environment-not-sampled");
        public string SleepObservationQuality { get; set; } = L(player, "server-sleep-not-sampled");
        public string VanillaProbe { get; set; } = L(player, "server-vanilla-not-sampled");
        public string[] VanillaEnvironmentLines { get; set; } = [L(player, "server-clothing-not-sampled"), L(player, "server-heat-not-sampled")];
        public SessionFaultKind? Fault { get; set; }
        public HashSet<SessionFaultKind> LoggedFaults { get; } = new();
        public long LifeGeneration { get; set; }
        public long CheckpointSequence { get; set; }
        public long SnapshotSequence { get; set; }
        public bool Deceased { get; set; }
        public bool PendingRespawn { get; set; }
        public bool HelloAccepted { get; set; }
        public bool FoodCatalogSent { get; set; }
        public int GroundwaterScanCursor;
        public double LastWaterQuerySeconds { get; set; } = double.NegativeInfinity;
        public double LastWaterInfoLogSeconds { get; set; } = double.NegativeInfinity;
        public bool WantsDiagnostics { get; set; }
        public double PublishAccumulator { get; set; }
        public double SaveAccumulator { get; set; }
        public double ProbeAccumulator { get; set; }
        public double DroppedServerSeconds { get; set; }
        public double LastDt { get; set; }
        public SettingsResponse? LastResponse { get; set; }
        public DebugValueResponse? LastDebugResponse { get; set; }
    }

    public ServerCoordinator(ICoreServerAPI api) => this.api = api;

    internal void LogProspectingWarning(string message) => api.Logger.Warning("[vsslssmod] {0}", message);
    internal void LogProspectingInfo(string message) => api.Logger.Notification("[vsslssmod] {0}", message);

    internal bool TryGetWellSupply(BlockPos pos, double requestedL, out WaterBatch batch, out double drawL)
    {
        batch = null!;
        drawL = 0;
        var well = FindWell(pos);
        if (well is null || !well.WellComplete || !double.IsFinite(requestedL) || requestedL <= 0) return false;
        drawL = Math.Min(requestedL, well.StorageL);
        if (drawL < .01 || !WellStructureResolver.TryFromHead(api.World.BlockAccessor, well.Pos, out var bounds)
            || groundwater?.Query(bounds.Bottom) is not { } state) return false;
        batch = well.StoredWater with { VolumeL = drawL };
        return batch.Validate() is null;
    }

    internal bool TryExtractWell(BlockPos pos, double amountL)
    {
        var well = FindWell(pos);
        if (well is null) return false;
        return well.TryDraw(amountL);
    }

    internal bool TryRefillWell(BlockPos bottom, double amountL) => groundwater?.TryExtract(bottom, amountL) == true;

    private BlockEntityWellHead? FindWell(BlockPos pos)
    {
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var target = pos.AddCopy(dx, 0, dz);
                if (api.World.BlockAccessor.GetBlockEntity(target) is BlockEntityWellHead head) return head;
                if (WellStructureResolver.TryFromWall(api.World.BlockAccessor, target, out var bounds)
                    && api.World.BlockAccessor.GetBlockEntity(bounds.Head) is BlockEntityWellHead owner) return owner;
            }
        return null;
    }

    internal GroundwaterState? QueryWellState(BlockPos pos) => groundwater?.Query(pos);

    internal void SendWellFillReveal(IServerPlayer player, BlockPos pos)
    {
        // Filling a container is a water transaction, not an excavation. It
        // must not create a groundwater particle reveal.
    }

    internal void NotifyGroundwaterExposure(IServerPlayer player, BlockPos pos)
    {
        if (groundwater is null || !sessions.TryGetValue(player.PlayerUID, out var session) || !session.HelloAccepted) return;
        var surface = api.World.BlockAccessor.GetTerrainMapheightAt(pos);
        var depthToBlockCenter = surface - (pos.Y + 0.5);
        if (depthToBlockCenter < -1) return;
        var state = groundwater.Query(pos);
        // The aquifer starts at the dynamic water table and extends through
        // its derived saturated thickness. A one-block tolerance avoids
        // flicker at the boundary while digging.
        if (depthToBlockCenter < state.WaterTableDepthM - 1
            || depthToBlockCenter > state.WaterTableDepthM + state.ThicknessM + 1) return;

        // DidBreakBlock is authoritative on the server. Only a newly opened
        // air cell with enough aquifer storage can expose seepage. The face
        // mask is calculated here so the client cannot manufacture exposure
        // by changing or misreading local blocks.
        if (api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id != 0
            || state.StorageL / Math.Max(state.CapacityL, 1) < 0.15) return;

        byte faceMask = 0;
        foreach (var face in GroundwaterFaces)
        {
            var neighbour = pos.AddCopy(face.Dx, 0, face.Dz);
            if (api.World.BlockAccessor.GetBlock(neighbour, BlockLayersAccess.Solid).Id == 0) continue;
            var neighbourState = groundwater.Query(neighbour);
            var neighbourSurface = api.World.BlockAccessor.GetTerrainMapheightAt(neighbour);
            var neighbourDepth = neighbourSurface - (neighbour.Y + 0.5);
            if (neighbourDepth < neighbourState.WaterTableDepthM - 1
                || neighbourDepth > neighbourState.WaterTableDepthM + neighbourState.ThicknessM + 1
                || neighbourState.StorageL / Math.Max(neighbourState.CapacityL, 1) < 0.15) continue;
            faceMask |= face.Bit;
        }
        if (faceMask == 0) return;

        var revealKey = new GroundwaterRevealKey(player.PlayerUID, pos.X, pos.Y, pos.Z, pos.dimension);
        if (groundwaterRevealCooldowns.TryGetValue(revealKey, out var lastSent)
            && ServerSeconds - lastSent < 30) return;
        groundwaterRevealCooldowns[revealKey] = ServerSeconds;
        if (groundwaterRevealCooldowns.Count > 2048)
            foreach (var old in groundwaterRevealCooldowns.Where(pair => ServerSeconds - pair.Value > 120).Select(pair => pair.Key).ToArray())
                groundwaterRevealCooldowns.Remove(old);

        channel.SendPacket(new GroundwaterReveal
        {
            SessionEpoch = session.Epoch,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Dimension = pos.dimension,
            SurfaceY = pos.Y + 0.5f,
            Radius = 4,
            DurationMs = 300000,
            ExcavationExposure = true,
            SaturatedFaceMask = faceMask,
            WaterContent = (float)Math.Clamp(state.StorageL / Math.Max(state.CapacityL, 1), 0, 1)
        }, player);
    }

    private void ScanGroundwaterExposure()
    {
        using var timing = exposureProbe?.Measure(api);
        foreach (var session in sessions.Values)
        {
            if (!session.HelloAccepted || session.Player.Entity is null) continue;
            var origin = session.Player.Entity.Pos.AsBlockPos;
            GroundwaterExposureScan.Visit(api.World.BlockAccessor, origin, ref session.GroundwaterScanCursor,
                pos => NotifyGroundwaterExposure(session.Player, pos));
        }
    }

    private static readonly GroundwaterFace[] GroundwaterFaces =
    [
        new(0, -1, 1), new(0, 1, 2), new(-1, 0, 4), new(1, 0, 8)
    ];

    private readonly record struct GroundwaterFace(int Dx, int Dz, byte Bit);

    public void Start()
    {
        try
        {
            var loaded = ReleaseConfigFile.Read<SeraphServerConfig>(api, ConfigFile);
            settings = new((loaded ?? settings.Config).ResolveUnits());
            if (loaded is null) api.StoreModConfig(settings.Config, ConfigFile);
        }
        catch (Exception ex)
        {
            startupFault = SessionFaultKind.ConfigurationRejected;
            settings = new(new() { Mode = "observe" });
            api.Logger.Error("[vsslssmod] Configuration rejected; fix vsslssmod.Server.json and restart. {0}", ex);
        }

        StartFramework();
        waterClassifier = new(api);
        WaterQuality.BindClassifier(api.World, waterClassifier);
        groundwater = new GroundwaterWorldStore(api);
        WaterQuality.BindGroundwater(api.World, groundwater);
        Pollution = new(api, () => settings.Config);
        WaterQuality.BindPollution(api.World, Pollution);
        prospecting = new ProspectingCompatibility(this);
        LoadFoods();
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, ExportFoodCoverage);
        channel = api.Network.RegisterChannel(Protocol.Channel)
            .RegisterMessageType<ClientHello>().RegisterMessageType<ServerSnapshot>()
            .RegisterMessageType<SettingsRequest>().RegisterMessageType<SettingsResponse>()
            .RegisterMessageType<WorldActionRequest>().RegisterMessageType<WaterInfoRequest>()
            .RegisterMessageType<WaterInfoResponse>().RegisterMessageType<GroundwaterReveal>()
            .RegisterMessageType<FoodCatalogMessage>()
            .RegisterMessageType<DebugValueRequest>().RegisterMessageType<DebugValueResponse>()
            .RegisterMessageType<ActivityLedger>().RegisterMessageType<NativeWorkReceipt>().RegisterMessageType<WorkIntent>()
            .RegisterMessageType<OrganicMistParticle>();
        channel.SetMessageHandler<WorkIntent>((player, packet) =>
        {
            if (packet is null || packet.SessionEpoch is null || packet.SessionEpoch.Length > 64
                || packet.Operations is null || packet.Operations.Length > 32 || !workInbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnWorkIntent(player, packet), "vsslssmod-work-intent");
        });
        channel.SetMessageHandler<NativeWorkReceipt>((player, packet) =>
        {
            if (packet is null || packet.SessionEpoch is null || packet.SessionEpoch.Length > 64
                || !workInbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnNativeWork(player, packet), "vsslssmod-native-work");
        });
        channel.SetMessageHandler<DebugValueRequest>((player, packet) =>
        {
            if (packet is null || packet.SessionEpoch is null || packet.SessionEpoch.Length > 64
                || packet.Field is null || packet.Field.Length > 40 || !inbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnDebugValue(player, packet), "vsslssmod-debug-value");
        });
        channel.SetMessageHandler<WaterInfoRequest>((player, packet) =>
        {
            if (packet is null || packet.SessionEpoch is null || packet.SessionEpoch.Length > 64 || !inbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnWaterInfo(player, packet), "vsslssmod-water-info");
        });
        channel.SetMessageHandler<WorldActionRequest>((player, packet) =>
        {
            if (packet is null || packet.SessionEpoch is null || packet.SessionEpoch.Length > 64
                || packet.Action is null || packet.Action.Length > 24 || !inbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnWorldAction(player, packet), "vsslssmod-world-action");
        });
        channel.SetMessageHandler<ClientHello>((player, packet) =>
        {
            if (packet is null || !inbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnHello(player, packet), "vsslssmod-hello");
        });
        channel.SetMessageHandler<SettingsRequest>((player, packet) =>
        {
            if (packet is null || packet.SessionEpoch is null || packet.SessionEpoch.Length > 64 || !inbound.TryAccept(player.PlayerUID)) return;
            api.Event.EnqueueMainThreadTask(() => OnSettings(player, packet), "vsslssmod-settings");
        });

        api.Event.PlayerReady += OnReady;
        api.Event.DidPlaceBlock += OnWaterBlockPlaced;
        api.Event.DidBreakBlock += OnWaterBlockBroken;
        api.Event.BreakBlock += CheckInjuryBlockBreak;
        api.Event.PlayerDisconnect += OnDisconnect;
        api.Event.PlayerDeath += OnDeath;
        api.Event.PlayerRespawn += OnRespawn;
        api.Event.GameWorldSave += OnWorldSave;
        api.Event.ServerSuspend += OnSuspend;
        api.Event.ServerResume += OnResume;
        listener = api.Event.RegisterGameTickListener(OnTick, 250);
        StartPerformance();
        RegisterCommands();
        api.Logger.Notification("[vsslssmod] {0} mode. Health/nutrition ownership follows capability policy; native oxygen reservoir and external revival are retained. Runtime hook report follows bridge installation.", settings.Config.Mode);
    }

    private void RegisterCommands()
    {
        api.ChatCommands.Create("vsslssmod").WithDescription(L(null, "server-command-description"))
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(args =>
            {
                var player = args.Caller.Player as IServerPlayer;
                var status = startupFault is null ? L(player, "status-boundary") : FaultMessage(player, startupFault.Value);
                return TextCommandResult.Success(L(player, "server-command-status", ModeName(player), sessions.Count, status, Seraph.Core.BuildInfo.Version));
            })
            .BeginSubCommand("check").WithDescription(L(null, "command-check")).RequiresPlayer()
                .HandleWith(args => PlayerCheck(args.Caller.Player as IServerPlayer)).EndSubCommand()
            .BeginSubCommand("drink").WithDescription(L(null, "command-drink")).RequiresPlayer()
                .HandleWith(args => Drink(args.Caller.Player as IServerPlayer)).EndSubCommand()
            .BeginSubCommand("pressure").WithDescription(L(null, "command-pressure")).RequiresPlayer()
                .HandleWith(args => BeginMedical(args.Caller.Player as IServerPlayer, "pressure")).EndSubCommand()
            .BeginSubCommand("clean").WithDescription(L(null, "command-clean")).RequiresPlayer()
                .HandleWith(args => BeginMedical(args.Caller.Player as IServerPlayer, "clean")).EndSubCommand()
            .BeginSubCommand("cancel").WithDescription(L(null, "command-cancel")).RequiresPlayer()
                .HandleWith(args => CancelMedical(args.Caller.Player as IServerPlayer)).EndSubCommand()
            .BeginSubCommand("mode").WithDescription(L(null, "command-mode")).RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.Word("mode"))
                .HandleWith(args => ChangeMode(args.Caller.Player as IServerPlayer, (string)args.Parsers[0].GetValue())).EndSubCommand()
            .BeginSubCommand("compat").WithDescription(L(null, "command-compat")).RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => TextCommandResult.Success(L(args.Caller.Player as IServerPlayer, "compat-heading") + "\n" +
                    string.Join("\n", capabilities.Assignments.Select(a => L(args.Caller.Player as IServerPlayer, "capability",
                        L(args.Caller.Player as IServerPlayer, "cap-" + a.Capability.ToString().ToLowerInvariant()),
                        L(args.Caller.Player as IServerPlayer, "owner-" + a.Owner.ToString().ToLowerInvariant()), a.Provider))))).EndSubCommand()
            .BeginSubCommand("probe").WithDescription(L(null, "server-command-probe-description")).RequiresPrivilege(Privilege.controlserver).RequiresPlayer()
                .HandleWith(args =>
                {
                    if (args.Caller.Player is not IServerPlayer player || !sessions.TryGetValue(player.PlayerUID, out var session))
                        return TextCommandResult.Error(L(args.Caller.Player as IServerPlayer, "server-player-not-ready"));
                    var probe = Snapshot(session, forceDiagnostics: true);
                    return TextCommandResult.Success(string.Join("\n", probe.ClockLines.Concat(probe.EnvironmentLines).Concat(probe.QualityLines)));
                }).EndSubCommand()
            .BeginSubCommand("speed").WithDescription(L(null, "server-command-speed-description")).RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.Double(L(null, "server-argument-multiplier")))
                .HandleWith(args =>
                {
                    return TextCommandResult.Error(L(args.Caller.Player as IServerPlayer, "server-calendar-speed-hint"));
                }).EndSubCommand();
    }

    private string WorldId => api.WorldManager.SaveGame.SavegameIdentifier;
    private double ServerSeconds => api.World.ElapsedMilliseconds / 1000d;

    private void OnReady(IServerPlayer player)
    {
        if (disposed || sessions.ContainsKey(player.PlayerUID)) return;
        var session = new Session(player) { Fault = startupFault, Framework = FrameworkState.Create(WorldId + ":" + player.PlayerUID) };
        var primary = player.GetModdata(StateKey);
        var backup = player.GetModdata(BackupKey);
        if (primary is not null || backup is not null)
        {
            var main = primary is null ? null : ShadowSaveCodec.Decode(primary, WorldId, player.PlayerUID);
            var last = backup is null ? null : ShadowSaveCodec.Decode(backup, WorldId, player.PlayerUID);
            // Any invalid existing copy blocks the life; never overwrite rejected bytes or reissue reserves.
            if (main is { Success: false } || last is { Success: false })
                SetFault(session, SessionFaultKind.SaveFormatUnsupported, main?.Error ?? last?.Error);
            else
            {
                var chosen = new[] { main?.Save, last?.Save }.Where(s => s is not null)
                    .OrderByDescending(s => s!.LifeGeneration).ThenByDescending(s => s!.CheckpointSequence).FirstOrDefault();
                if (chosen is null) SetFault(session, SessionFaultKind.SaveInvalid, main?.Error ?? last?.Error);
                else
                {
                    session.Profile = chosen.Profile;
                    session.State = chosen.State;
                    session.Framework = chosen.Framework;
                    session.Interactions = chosen.Interactions;
                    session.LifeGeneration = chosen.LifeGeneration;
                    session.CheckpointSequence = chosen.CheckpointSequence;
                    session.Deceased = chosen.Deceased;
                    if (main?.Success != true && primary is not null)
                    {
                        if (primary.Length <= ShadowSaveCodec.MaxBytes) player.SetModdata("vsslssmod:quarantined", primary);
                        api.Logger.Warning("[vsslssmod] Recovered private state from lastgood for {0}: {1}", player.PlayerUID, main?.Error);
                    }
                }
            }
        }
        // Calendar periods are already normalized biological seconds. Applying the
        // calendar rate again made temperature respond thirty times too slowly.
        session.Kernel = new(session.Profile, new() {
            BasalWaterLossGPerGameHour = settings.Config.BasalWaterLossGPerGameHour,
            });
        session.Scheduler = CreateScheduler();
        session.ActionEnergy = new();
        session.HeldWork = new();
        session.WorkAcknowledgements = new(); session.ExertionLife++;
        session.IntentSequence = 0; session.IntentHeld = false; session.IntentAt = double.NegativeInfinity;
        session.NativeWork = new(); session.LastMiningTarget = null; session.LastAttackCharge = double.NegativeInfinity;
        session.RecentBrokenBlocks.Clear();
        session.ActionPowerW = session.ActivityPowerW = session.DemandPowerW = 0;
        session.WorkIntervals = new();
        session.RunningPace = new(); session.MovementActivity = ExertionKind.Standing;
        session.RecoveryClock = new();
        session.JumpCooldownRemaining = session.SprintExhausted ? settings.Config.Exertion.ExhaustedJumpCooldownSeconds : 0;
        session.CalendarWorkIntervals = new();
        session.LastDiscreteWorkAt = double.NegativeInfinity;
        session.ActionUntilSeconds = session.MiningUntil = session.StrongUntil = double.NegativeInfinity;
        session.LedgerInitialized = false;
        ClearClosedEyeRest(session);
        // A confirmed vanilla revival can finish after a disconnect and before this ready event.
        // The saved deceased marker supplies the prior death; never infer death from a missing save.
        session.PendingRespawn = session.Deceased && player.Entity?.Alive == true;
        sessions.Add(player.PlayerUID, session);
        session.ClinicalWarnings.Seed(ClinicalWarningPolicy.Evaluate(session.Framework, session.State, session.Profile, settings.Config.Exertion));
        RestoreNativeTiredness(session);
        ResetSleepTimeline(session);
        AttachObserver(session);
        Trace(session, "ready");
        api.Logger.Notification("[vsslssmod] Player session initialized; connection state {0}.", player.ConnectionState);
    }

    private CalendarTimeScheduler CreateScheduler() => new();

    private void OnDisconnect(IServerPlayer player)
    {
        if (sessions.TryGetValue(player.PlayerUID, out var existing) && !ReferenceEquals(existing.Player, player)) return;
        inbound.Remove(player.PlayerUID);
        workInbound.Remove(player.PlayerUID);
        performanceInbound.Remove(player.PlayerUID);
        if (!Current(player, out var session)) return;
        ConfirmRespawn(session);
        Save(session);
        CleanupSession(session);
        sessions.Remove(player.PlayerUID);
    }

    private void OnDeath(IServerPlayer player, DamageSource _)
    {
        if (!Current(player, out var session)) return;
        session.Deceased = true;
        session.SleepEyes.Reset();
        smoke?.Ownership(player.Entity, false);
        session.Framework = session.Framework with { SmokeDose = 0 }; session.SmokeSampleValid = false;
        ClearFlowWindow(session);
        session.ClinicalWarnings.Reset(); session.PendingInsectBites = 0;
        session.Defecation = null; session.GroundDrink = null; session.ProduceWash = null; session.WellBucketRaise = null; session.Pressure = null;
        session.PendingRespawn = false;
        CancelWash(session);
        RemoveEffects(session);
        session.DroppedServerSeconds += session.Scheduler.Schedule(0, paused: true).DroppedServerSeconds;
        Save(session);
        Trace(session, "death");
    }

    private void OnRespawn(IServerPlayer player)
    {
        if (!Current(player, out var session) || !session.Deceased || session.Fault is not null) return;
        // In 1.22.x this event starts teleport/loading; it does not confirm Entity.Revive().
        session.PendingRespawn = true;
    }

    private void ConfirmRespawn(Session session)
    {
        if (!session.Deceased || session.Fault is not null || session.Player.Entity?.Alive != true) return;
        session.LifeGeneration = checked(session.LifeGeneration + 1);
        session.SleepEyes.Reset();
        session.Deceased = false;
        session.PendingRespawn = false;
        // A new life has fresh transient state. Only stable constitution/profile survive.
        session.State = BodyState.Create(session.Profile);
        session.Framework = FrameworkState.Create(WorldId + ":" + session.Player.PlayerUID) with { Constitution = session.Framework.Constitution };
        session.Interactions = new();
        session.GroundDrink = null; session.ProduceWash = null; session.Defecation = null; session.WellBucketRaise = null; session.Pressure = null;
        session.ClinicalWarnings.Seed(ClinicalWarningPolicy.Evaluate(session.Framework, session.State, session.Profile, settings.Config.Exertion));
        session.PendingInsectBites = 0;
        session.WaterWashLatched = false;
        session.LastDrinkHoldSeconds = double.NegativeInfinity;
        session.LastDrinkSeconds = double.NegativeInfinity;
        session.PositionKnown = false;
        CancelWash(session);
        RemoveEffects(session);
        if (session.Player.Entity.GetBehavior<Vintagestory.GameContent.EntityBehaviorHealth>() is { } health) health.ActiveDoTEffects.Clear();
        session.LastHurtCounter = session.Player.Entity.WatchedAttributes.GetInt("onHurtCounter");
        session.LastDamageKind = null;
        session.Scheduler = CreateScheduler();
        ResetSleepTimeline(session);
        session.ActionEnergy = new();
        session.HeldWork = new();
        session.WorkAcknowledgements = new(); session.ExertionLife++;
        session.IntentSequence = 0; session.IntentHeld = false; session.IntentAt = double.NegativeInfinity;
        session.NativeWork = new(); session.LastMiningTarget = null; session.LastAttackCharge = double.NegativeInfinity;
        session.RecentBrokenBlocks.Clear();
        session.ActionPowerW = session.ActivityPowerW = session.DemandPowerW = 0;
        session.WorkIntervals = new();
        session.RunningPace = new(); session.MovementActivity = ExertionKind.Standing;
        session.RecoveryClock = new();
        session.JumpCooldownRemaining = session.SprintExhausted ? settings.Config.Exertion.ExhaustedJumpCooldownSeconds : 0;
        session.CalendarWorkIntervals = new();
        session.LastDiscreteWorkAt = double.NegativeInfinity;
        session.ActionUntilSeconds = session.MiningUntil = session.StrongUntil = double.NegativeInfinity;
        session.SprintExhausted = session.LedgerInitialized = false;
        ClearClosedEyeRest(session);
        session.Environment = null;
        ClearFlowWindow(session);
        session.LastSampleSeconds = double.NegativeInfinity;
        Save(session);
        ProjectPhysiology(session.Player.Entity);
        SendSnapshot(session);
        Trace(session, "respawn");
        api.Logger.Notification("[vsslssmod] Confirmed shadow respawn supplement: {0}, generation {1}.", session.Player.PlayerUID, session.LifeGeneration);
    }

    private EnumSuspendState OnSuspend()
    {
        if (!paused)
            foreach (var session in sessions.Values)
            {
                session.DroppedServerSeconds += session.Scheduler.Schedule(0, paused: true).DroppedServerSeconds;
                CancelWash(session);
                session.Defecation = null; session.GroundDrink = null; session.ProduceWash = null; session.WellBucketRaise = null; session.Pressure = null;
                ClearGroundSquat(session);
                session.PositionKnown = false;
            }
        paused = true;
        lastPerformanceTick = 0;
        return EnumSuspendState.Ready;
    }
    private void OnResume()
    {
        paused = false;
        lastPerformanceTick = 0;
        skipNextDelta = true;
        lastCalendarHours = double.NaN;
        foreach (var s in sessions.Values) ResetSleepTimeline(s);
    }

    private readonly SlowCallbackProbe tickProbe = new("server.coordinator.tick");
    private readonly SlowCallbackProbe exposureProbe = new("server.groundwater.exposure", 50);
    private readonly SlowCallbackProbe sampleProbe = new("server.environment.sample", 50);
    private readonly SlowCallbackProbe saveProbe = new("server.coordinator.save");
    private readonly SlowCallbackProbe buildSnapshotProbe = new("server.snapshot.build", 50);
    private void OnTick(float dt)
    {
        if (disposed || paused) return;
        using var timing = tickProbe?.Measure(api);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var hours = api.World.Calendar.TotalHours;
        calendarDelta = double.IsFinite(lastCalendarHours) ? hours - lastCalendarHours : 0;
        lastCalendarHours = hours;
        if (skipNextDelta) { dt = 0; calendarDelta = 0; skipNextDelta = false; }
        if (!float.IsFinite(dt) || dt < 0) return;
        ScanGroundwaterExposure();
        foreach (var session in sessions.Values)
        {
            try { TickSession(session, dt); }
            catch (Exception ex)
            {
                SetFault(session, SessionFaultKind.UnexpectedSessionError, ex);
            }
        }
        diagnostics.RecordRound(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, sessions.Count);
    }

    // Native bed callbacks can run before this tick. The recorded period, rather than
    // the current mount state, decides whether the elapsed time was spent sleeping.
    private bool SettleCalendarPeriod(Session session, CalendarSleepPeriod period)
    {
        var entity = session.Player.Entity;
        var sleeping = period.Sleeping;
        var elapsed = period.Hours / Math.Max(1, api.World.Calendar.HoursPerDay) * 86400;
        // Keep each work batch bounded, but finish the earned calendar interval now.
        // Vanilla sleep can advance thousands of model seconds in one callback.
        do
        {
            var budget = session.Scheduler.Schedule(elapsed);
            elapsed = 0;
            session.DroppedServerSeconds += budget.DroppedServerSeconds;
            if (!budget.Accepted) { SetFault(session, SessionFaultKind.TimeStepRejected, budget.Error); break; }
            session.Framework = session.Framework with {
                TreatmentEffectsEnabled = settings.Config.EnableClinicalEffects && settings.Config.Mode == "survival" };
            var activity = sleeping || session.Teleported ? 0 : session.DemandPowerW;
            var bedQuality = sleeping ? period.BedQuality : session.Framework.BedQuality;
            if (session.ClosedEyeRest == ClosedEyeRestMode.Seated) bedQuality = Math.Min(bedQuality, .22);
            else if (session.ClosedEyeRest == ClosedEyeRestMode.Reclined) bedQuality = Math.Min(bedQuality, .42);
            for (var i = 0; i < budget.SubstepCount; i++)
            {
                ValidatePressure(session);
                var inputs = PrepareStepInputs(session, session.Framework, session.State, sleeping, bedQuality, activity, budget.SubstepBioSeconds);
                var result = session.Kernel.Advance(session.State, session.Environment!, inputs, budget.SubstepBioSeconds);
                if (!result.Accepted) { SetFault(session, SessionFaultKind.PhysiologyStepRejected, result.Error); break; }
                var frameworkResult = AdvanceFramework(session, result.State, inputs, budget.SubstepBioSeconds, out var insectBites,
                    paidActivityEnergyJ: result.Diagnostics.ActivityEnergyJ);
                if (!frameworkResult.Accepted) { SetFault(session, SessionFaultKind.PhysiologyStepRejected, frameworkResult.Error); break; }
                var previousNutrition = session.Framework.Nutrition;
                var nutrition = SettleNutrition(previousNutrition, session.State, result, budget.SubstepBioSeconds,
                    Math.Max(0, BowelSettlement.MaximumPendingG - session.Interactions.PendingBowelUnits));
                if (sleeping) NotifySleepDanger(session, result.State, frameworkResult.State);
                session.Thermal = session.Thermal.Add(result.Diagnostics);
                session.PaidEnergyJ += result.Diagnostics.MetabolicEnergyJ;
                session.SettledSeconds += budget.SubstepBioSeconds;
                var recovery = SettleClinicalStep(session, session.Framework, frameworkResult.State with { Nutrition = nutrition },
                    result.State, inputs, budget.SubstepBioSeconds, session.State, result.Diagnostics, Math.Max(0, BowelSettlement.CapacityG - session.Interactions.BowelUnits));
                var clearedMl = recovery.State.Blood.FiltrationMlPerGameHour * budget.SubstepBioSeconds / 3600;
                var bloodWasteLostMl = session.Framework.Blood.WasteMl * Math.Clamp(result.Diagnostics.WholeBloodLossL
                    / Math.Max(1e-9, session.State.BloodVolumeL(session.Profile)), 0, 1);
                // These paid repairs draw glycogen first. Protein surplus is deposited afterwards,
                // so a net reserve delta would hide fuel spent during this same step.
                var recoveryGlycogenKJ = Math.Min(result.State.GlycogenEnergyKJ, recovery.EnergyKJ);
                session.Flows = session.Flows.Add(result.Diagnostics, recovery.EnergyKJ,
                    recovery.State.Blood.WasteMl - session.Framework.Blood.WasteMl + clearedMl + bloodWasteLostMl, clearedMl,
                    recoveryGlycogenKJ, recovery.EnergyKJ - recoveryGlycogenKJ);
                session.State = recovery.Body;
                session.Framework = recovery.State;
                // Consume observations only after the complete clinical substep was accepted.
                session.PendingInsectBites = Math.Min(1000000, session.PendingInsectBites + insectBites);
                session.PaidEnergyJ += recovery.EnergyKJ * 1000;
                // Voluntary K rest also restores fatigue/stamina at low sleep
                // pressure. Only a toggle, movement or invalid posture ends it.
                session.Interactions = BowelSettlement.Advance(session.Interactions,
                    previousNutrition.BowelResidueG(nutrition), recovery.BowelG, budget.SubstepBioSeconds);
                if (OwnsPhysiology(entity)) { CheckOwnDeath(entity); if (session.Deceased || !entity.Alive) break; }
            }
        } while (session.Scheduler.PendingSeconds > 0 && session.Fault is null && !session.Deceased && entity.Alive);
        return session.Fault is null && !session.Deceased && entity.Alive;
    }

    private void TickSession(Session session, double dt)
    {
        session.LastDt = dt;
        var entity = session.Player.Entity;
        if (entity is null) return;
        AdvanceVomitEffect(session, dt);
        AttachObserver(session);
        ObserveConfirmedHurt(session);
        ConfirmRespawn(session);
        SyncDowned(entity);
        ObservePosition(session);
        ValidateClosedEyeRest(session);
        if (ServerSeconds - session.LastSampleSeconds >= 1) Sample(session);
        var tiredness = entity.WatchedAttributes.GetTreeAttribute("tiredness");
        // Vanilla 1.22.7 creates the tree without isSleeping; its getter defaults an absent key to awake.
        // A missing tree is unknown, while an existing tree's missing key has a verified vanilla meaning.
        var sleepKnown = tiredness is not null;
        var nativeBedSleeping = sleepKnown && tiredness!.GetInt("isSleeping") > 0;
        var closedEyeResting = session.ClosedEyeRest != ClosedEyeRestMode.None;
        session.SleepObservationQuality = L(session.Player, !sleepKnown ? "server-sleep-unknown"
            : nativeBedSleeping ? "server-sleep-sleeping" : closedEyeResting ? "server-sleep-closed-eye" : "server-sleep-awake");
        var mayAdvance = (settings.Config.Mode is "shadow" or "survival") && session.Fault is null && !session.Deceased && entity.Alive
            && IsSurvivalTestPlayer(session.Player)
            && api.World.BlockAccessor.GetChunkAtBlockPos(entity.Pos.AsBlockPos) is not null
            && sleepKnown && session.Environment is not null && ServerSeconds - session.LastGoodSampleSeconds <= 5;
        // Consume the native transition history even when physiology is frozen, so a later
        // survival interval cannot inherit an old sleep boundary.
        var endHours = api.World.Calendar.TotalHours;
        var periods = session.SleepTimeline.Consume(endHours - Math.Max(0, calendarDelta), endHours);
        TickExertion(session, mayAdvance, dt);
        TickSmoke(session, mayAdvance, dt);
        // Match real work intervals to calendar tiers without dividing by accelerated biological time.
        var realStart = ServerSeconds - dt;
        var boundaries = session.CalendarWorkIntervals.Boundaries(realStart, ServerSeconds)
            .Concat(session.ActionEnergy.Boundaries(realStart, ServerSeconds)).Append(realStart).Append(ServerSeconds)
            .Distinct().Order().ToArray();
        if (mayAdvance)
        {
            (session.Framework, session.State) = frameworkKernel.AdvanceAdrenaline(session.Framework, session.State, dt);
            session.Framework = session.Framework with { Pain = Treatments.Pain(session.Framework,
                BloodCirculation.LactateCapacityG(session.State, session.Profile)) };
        }
        // Publish accepted flows over the complete snapshot interval. Previously
        // each 100 ms callback discarded the other nine samples, aliasing work
        // packet boundaries into large apparent changes in hourly consumption.
        var totalHours = periods.Sum(p => p.Hours);
        BeginFlowWindow(session, mayAdvance && totalHours > 0);
        var periodStart = realStart;
        foreach (var period in periods)
        {
            var periodEnd = periodStart + (totalHours > 0 ? dt * period.Hours / totalHours : 0);
            var cuts = boundaries.Where(t => t > periodStart && t < periodEnd).Prepend(periodStart).Append(periodEnd).ToArray();
            for (var i = 1; i < cuts.Length; i++)
            {
                var duration = cuts[i] - cuts[i - 1];
                if (duration <= 0) continue;
                session.RestFraction = session.CalendarWorkIntervals.RestSeconds(cuts[i - 1], cuts[i]) / duration;
                session.DemandPowerW = session.ActionEnergy.Consume(cuts[i - 1], cuts[i],
                    mayAdvance && !session.Teleported ? session.ActivityPowerW : 0);
                if (mayAdvance && !SettleCalendarPeriod(session, period with { Hours = period.Hours * duration / Math.Max(1e-9, periodEnd - periodStart) })) break;
            }
            periodStart = periodEnd;
            if (session.Fault is not null || session.Deceased) break;
        }
        // Zero calendar delta (pause/time reset) must still retire past wall-clock entries.
        SettleSmokeInjury(session);
        session.CalendarWorkIntervals.RestSeconds(realStart, ServerSeconds);
        session.ActionEnergy.Consume(realStart, ServerSeconds);
        UpdateEnergySnapshot(session);
        AdvanceSleepEyes(session, mayAdvance, dt);
        ValidatePressure(session);
        PublishActivity(session, dt);
        ProjectSleep(session);
        ProjectPhysiology(entity);
        if (OwnsBleeding(entity)) BleedingVisualBridge.Sync(entity, !session.Deceased && entity.Alive && ClinicalBleeding(session) > 0);
        AnnounceClinicalEffects(session);
        TickActionsAndEffects(session, mayAdvance && session.Fault is null);
        TickWorldActions(session, mayAdvance && session.Fault is null, dt);
        // Time accumulators are capped independently of an oversized game callback.
        var acceptedReal = Math.Min(dt, 2);
        session.PublishAccumulator += acceptedReal;
        session.SaveAccumulator += acceptedReal;
        session.ProbeAccumulator += acceptedReal;
        if (session.PublishAccumulator >= 1)
        {
            session.PublishAccumulator = 0; SendSnapshot(session); session.ResetFlowWindow = true;
        }
        if (session.SaveAccumulator >= settings.Config.CheckpointSeconds) { session.SaveAccumulator = 0; Save(session); }
        if (settings.Config.TelemetryEnabled && session.ProbeAccumulator >= 10)
        {
            session.ProbeAccumulator = 0;
            Trace(session, "tick");
        }
    }

    private static void ClearFlowWindow(Session session)
    {
        BeginFlowWindow(session, false);
        UpdateEnergySnapshot(session);
        session.ActivityWindowWallSeconds = session.ActivityWindowBioSeconds = session.ActivityWindowPaidEnergyJ = 0;
        session.ActivityWindowStaminaDelta = session.ActivityWindowOxygenDelta = session.ActivityWindowFatigueDelta = 0;
        session.ActivityRateEnergy = session.ActivityRateStamina = session.ActivityRateOxygen = session.ActivityRateFatigue = 0;
        session.LedgerInitialized = false;
        session.ResetFlowWindow = true;
    }

    private static void BeginFlowWindow(Session session, bool advancing)
    {
        if (session.ResetFlowWindow || !advancing)
        {
            session.Thermal = new(); session.Flows = new(); session.ResetFlowWindow = false;
        }
        session.PhysiologyAdvancing = advancing;
    }

    private static void UpdateEnergySnapshot(Session session)
    {
        var flows = session.Flows;
        session.HasEnergySnapshot = session.PhysiologyAdvancing && flows.Seconds > 0;
        session.EnergyBasalKcalPerGameHour = flows.KcalPerHour(flows.BasalJ);
        session.EnergyColdKcalPerGameHour = flows.KcalPerHour(flows.ColdJ);
        session.EnergyActivityKcalPerGameHour = flows.KcalPerHour(flows.ActivityJ);
        session.EnergyRecoveryKcalPerGameHour = flows.KcalPerHour(flows.RecoveryJ);
        session.EnergyTotalKcalPerGameHour = session.EnergyBasalKcalPerGameHour + session.EnergyColdKcalPerGameHour
            + session.EnergyActivityKcalPerGameHour + session.EnergyRecoveryKcalPerGameHour;
    }

    private void Sample(Session session)
    {
        using var timing = sampleProbe?.Measure(api);
        session.LastSampleSeconds = ServerSeconds;
        // Optional diagnostics have independent failure handling and never gate or change physical inputs.
        session.VanillaEnvironmentLines = VanillaEnvironmentProbe.Capture(session.Player, session.LastSampleSeconds);
        try
        {
            var entity = session.Player.Entity;
            var sample = MicroclimateAdapter.Sample(api, session.Player, settings.Config);
            if (sample is null) { session.SampleQuality = L(session.Player, "server-climate-unavailable"); return; }
            var smokeKnown = session.SmokeSampleValid && smoke?.Ready == true && entity.Pos.Dimension == 0;
            var localGas = gases?.Sample(entity.Pos.XYZ.AddCopy(entity.LocalEyePos), entity.Pos.Dimension) ?? default;
            session.Environment = SmokeGasCoupling.Apply(sample.Environment, gases?.Ready == true, localGas);
            session.Environment = session.Environment with { AirPollution = smokeKnown ? Math.Clamp(session.SmokeConcentration, 0, 1) : 0 };
            session.Sheltered = sample.Sheltered;
            session.Sanitation = ObserveSanitation(session, sample);
            session.LastGoodSampleSeconds = ServerSeconds;
            var bodyTemp = entity.WatchedAttributes.GetTreeAttribute("bodyTemp");
            var oxygen = entity.WatchedAttributes.GetTreeAttribute("oxygen");
            session.VanillaProbe = L(session.Player, "server-vanilla-probe", Number(bodyTemp?.GetFloat("bodytemp"), "0.00"),
                Number(entity.WatchedAttributes.GetFloat("wetness"), "0.00"), Number(oxygen?.GetFloat("currentoxygen"), "0"), Number(oxygen?.GetFloat("maxoxygen"), "0"));
            session.SampleQuality = L(session.Player, "server-environment-quality", Number(sample.Rainfall, "0.00"), Number(sample.RawWind, "0.000"));
        }
        catch (Exception ex) { session.SampleQuality = L(session.Player, "server-sample-failed", ex.GetType().Name); }
    }

    private bool Current(IServerPlayer player, out Session session)
    {
        if (!disposed && sessions.TryGetValue(player.PlayerUID, out var current) && ReferenceEquals(current.Player, player))
        { session = current; return true; }
        session = null!;
        return false;
    }

    private void OnHello(IServerPlayer player, ClientHello packet)
    {
        if (disposed) return;
        if (packet.ProtocolVersion != Protocol.Version)
        {
            channel.SendPacket(new ServerSnapshot { Summary = L(player, "server-protocol-mismatch") }, player);
            return;
        }
        // PlayerReady is a mod-signalled event. Recover a missing event only
        // when the authoritative connection is Playing and its entity exists.
        // A client hello alone is never permission to initialize physiology.
        if (!sessions.ContainsKey(player.PlayerUID) && player.ConnectionState == EnumClientState.Playing
            && player.Entity is not null && ReferenceEquals(api.World.PlayerByUid(player.PlayerUID), player))
        {
            api.Logger.Notification("[vsslssmod] Recovering missing PlayerReady session from server Playing state.");
            OnReady(player);
        }
        if (!Current(player, out var session))
        {
            channel.SendPacket(new ServerSnapshot { Summary = L(player, "server-player-not-ready") }, player);
            return;
        }
        if (!session.HelloAccepted) api.Logger.Notification("[vsslssmod] Client handshake accepted; protocol {0}.", Protocol.Version);
        session.HelloAccepted = true;
        session.WantsDiagnostics = packet.RequestDiagnostics;
        SendSnapshot(session);
        if (!session.FoodCatalogSent)
        {
            channel.SendPacket(new FoodCatalogMessage { SessionEpoch = session.Epoch,
                Json = System.Text.Json.JsonSerializer.Serialize(foods) }, player);
            session.FoodCatalogSent = true;
        }
    }

    private void OnSettings(IServerPlayer player, SettingsRequest packet)
    {
        if (!Current(player, out var session) || !session.HelloAccepted || packet.ProtocolVersion != Protocol.Version
            || packet.SessionEpoch != session.Epoch || packet.RequestId <= 0) return;
        if (session.LastResponse is { } previous && packet.RequestId <= previous.RequestId)
        { if (packet.RequestId == previous.RequestId) channel.SendPacket(previous, player); return; }
        var error = ApplySettings(player, player.HasPrivilege(Privilege.controlserver), packet.ExpectedRevision, packet.PhysiologySpeed, packet.TelemetryEnabled);
        session.LastResponse = new()
        {
            SessionEpoch = session.Epoch, RequestId = packet.RequestId, Accepted = error is null,
            Message = error ?? L(player, "server-settings-applied")
        };
        channel.SendPacket(session.LastResponse, player);
        SendSnapshot(session);
    }

    private string? ApplySettings(IServerPlayer? player, bool authorized, long revision, double speed, bool telemetry)
    {
        var error = settings.ValidateChange(authorized, revision, speed);
        if (error is not null) return ValidationMessage(player, error);
        if (startupFault is not null) return FaultMessage(player, startupFault.Value);
        try
        {
            if (!SaveAll()) return L(player, "server-checkpoint-staging-failed");
            var candidate = settings.Config with { PhysiologySpeed = speed, TelemetryEnabled = telemetry };
            api.StoreModConfig(candidate, ConfigFile);
            settings.Commit(speed, telemetry);
            api.Logger.Notification("[vsslssmod] Shadow settings revision {0}: speed={1}, telemetry={2}.", settings.Revision, speed, telemetry);
            return null;
        }
        catch (Exception ex) { api.Logger.Error("[vsslssmod] Settings save failed: {0}", ex); return L(player, "server-settings-save-failed"); }
    }

    private ServerSnapshot Snapshot(Session session, bool forceDiagnostics = false)
    {
        using var timing = buildSnapshotProbe?.Measure(api);
        var admin = session.Player.HasPrivilege(Privilege.controlserver);
        var diagnostics = admin && (session.WantsDiagnostics || forceDiagnostics);
        var state = session.State;
        var sample = session.Environment;
        return new()
        {
            SessionEpoch = session.Epoch, Sequence = ++session.SnapshotSequence,
            Summary = session.Fault is not null ? L(session.Player, "server-summary-paused")
                : string.Join("\n", PublicLines(session)),
            PublicLines = PublicLines(session),
            BodyRegions = BodyRegions(session), Gauges = HudGauges(session),
            StatusMetrics = StatusMetrics(session), TemperatureMetrics = TemperatureMetrics(session),
            RecoveryMetrics = RecoveryMetrics(session), StatusIcons = StatusIcons(session),
            FunctionMetrics = FunctionMetrics(session),
            ClinicalEffects = ClinicalProjection(session),
            OwnWoundCare = OwnsWoundCare(session.Player.Entity),
            SymptomVisuals = settings.Config.EnableClinicalEffects && settings.Config.EnableSymptomVisuals && OwnsPhysiology(session.Player.Entity) && !session.Deceased,
            DrowsyMovement = ClosedEyeTurnEnabled(session),
            SleepPressure = state.SleepPressure,
            SleepEyes = SleepEyeProjection(session),
            Adrenaline = session.Framework.AdrenalineLevel > 0
                ? Math.Clamp(session.Framework.AdrenalineRemainingSeconds / 60, 0, 1) : 0,
            EffectiveHeartRateBpm = AdrenalineEffects.HeartRate(session.Framework),
            VentilationMultiplier = AdrenalineEffects.Ventilation(session.Framework),
            BedRest = ProjectBedRest(session),
            IsSleeping = session.Player.Entity.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0
                || session.ClosedEyeRest != ClosedEyeRestMode.None,
            IsClosedEyeResting = session.ClosedEyeRest != ClosedEyeRestMode.None,
            CleanRemainingGameHours = session.Framework.CleanRemainingGameHours,
            BathedRemainingGameHours = session.Framework.BathedRemainingGameHours,
            HydrationStage = Hydration.Stage(session.State, session.Profile),
            SleepBlurSeverity = session.Framework.SleepBlurSeverity,
            AlertnessReliefSeconds = Math.Clamp(session.Framework.AlertnessReliefUntilBioSeconds - state.ElapsedBioSeconds, 0, RestFeedback.IntakeReliefSeconds),
            Functions = ActionFunctions(session.Player.Entity), Exertion = ExertionProjection(session),
            HasUnlocatedInjury = session.Framework.Wounds.Any(w => Anatomy.VitalIndex(w.Region) < 0),
            WaterLines = WaterLines(session), InteractionLines = InteractionLines(session),
            // Water information travels independently of the one-second body snapshot.
            ActionProgress = ActionProgress(session),
            Pressure = PressureProjection(session),
            Nutrients = NutrientProjection(session),
            GutMassG = session.Framework.Nutrition.ContentsMassG(session.State.GutWaterL),
            GutMaximumG = 5000,
            GutWaterL = session.State.GutWaterL,
            StomachCompositionG = [session.State.GutWaterL * 1000, session.Framework.Nutrition.Pending[0],
                session.Framework.Nutrition.Pending[1], session.Framework.Nutrition.Pending[2],
                session.Framework.Nutrition.Pending[3], session.Framework.Nutrition.StomachWasteG],
            BowelUnits = session.Interactions.BowelUnits,
            EnergyKJ = state.GlycogenEnergyKJ + state.FatEnergyKJ,
            EnergyMaximumKJ = session.Profile.GlycogenCapacityKJ + session.Profile.FatCapacityKJ,
            GlycogenEnergyKJ = state.GlycogenEnergyKJ,
            GlycogenCapacityKJ = session.Profile.GlycogenCapacityKJ,
            FatEnergyKJ = state.FatEnergyKJ,
            FatCapacityKJ = session.Profile.FatCapacityKJ,
            GutEnergyKJ = state.GutEnergyKJ,
            GutEnergyMaximumKJ = session.Profile.MaximumGutEnergyKJ,
            PanelStatusLines = PanelStatusLines(session),
            PanelTemperatureLines = PanelTemperatureLines(session),
            PanelNutritionLines = PanelNutritionLines(session),
            PanelRecoveryLines = PanelRecoveryLines(session),
            PanelHydrationLines = PanelHydrationLines(session),
            PanelWoundLines = PanelWoundLines(session),
            CanViewDiagnostics = diagnostics, CanEditSettings = diagnostics && startupFault is null,
            DebugValues = diagnostics ? DebugValues(session) : [],
            PollutionDebugVolumes = diagnostics ? Pollution?.DebugVolumes(session.Player.Entity.Pos.AsBlockPos) ?? [] : [],
            LifeGeneration = session.LifeGeneration,
            SettingsRevision = diagnostics ? settings.Revision : 0, PhysiologySpeed = settings.Config.PhysiologySpeed,
            TelemetryEnabled = diagnostics && settings.Config.TelemetryEnabled,
            PhysiologyLines = !diagnostics ? [] : [
                L(session.Player, "server-physiology-temperature", Number(state.CoreTemperatureC, "0.000"), Number(state.SkinTemperatureC, "0.000")),
                L(session.Player, "server-physiology-water", Number(state.BodyWaterL, "0.000"), Number(state.GutWaterL, "0.000")),
                L(session.Player, "server-physiology-blood", Number(state.BloodVolumeL(session.Profile), "0.000"), Number(state.RbcVolumeL, "0.000"), Number(state.PlasmaWaterL, "0.000")),
                L(session.Player, "server-physiology-energy", Number(state.GlycogenEnergyKJ, "0.0"), Number(state.FatEnergyKJ, "0.0")),
                L(session.Player, "server-physiology-sleep", Number(state.SleepPressure, "0.000"), Number(state.SkinFilmWaterKg, "0.0000")),
                L(session.Player, "server-drowsy-diagnostics", Number(state.SleepPressure, "P1"),
                    ClosedEyeTurnEnabled(session)),
                L(session.Player, "diagnostic-framework", session.Framework.EventSequence, Number(session.Framework.PerfusionTrend, "0.000"), Number(session.Framework.OxygenAvailability, "0.000"), session.Framework.Diseases.Length),
                L(session.Player, "diagnostic-hit", session.LastResolvedRegion, session.LastHitQuality, session.LastHitPosition, Number(session.LastDamageAmount, "0.###")), session.LastCombatStages, .. InteractionLines(session)],
            EnvironmentLines = !diagnostics ? [] : [sample is null ? L(session.Player, "server-environment-unknown") :
                L(session.Player, "server-environment-values", Number(sample.AirTemperatureC, "0.00"), Number(sample.RelativeHumidity, "P0"), Number(sample.WindSpeedMps, "0.00")),
                sample is null ? L(session.Player, "server-environment-location-unknown") :
                L(session.Player, "server-environment-location-values", Number(sample.RadiantTemperatureC, "0.00"), Number(sample.RoomHeatDeltaC, "0.00"),
                    Number(sample.RoomVentilation, "P0"), Number(sample.AirPollution, "P0"), Number(sample.EnvironmentalContamination, "P0"))],
            ClockLines = !diagnostics ? [] : [
                L(session.Player, "server-clock-callback", Number(session.LastDt, "0.0000"), Number(calendarDelta, "0.000000")),
                L(session.Player, "server-clock-biological", Number(state.ElapsedBioSeconds, "0.00"), Number(settings.Config.PhysiologySpeed, "0.00"), Number(session.DroppedServerSeconds, "0.000")),
                L(session.Player, "server-clock-checkpoint", session.LifeGeneration, session.CheckpointSequence)],
            QualityLines = !diagnostics ? [] : [session.SampleQuality, session.SleepObservationQuality,
                session.Fault is null ? L(session.Player, "server-no-session-fault") : L(session.Player, "server-session-fault", FaultMessage(session.Player, session.Fault.Value)),
                L(session.Player, "status-boundary"),
                .. capabilities.Assignments.Select(a => L(session.Player, "capability", L(session.Player, "cap-" + a.Capability.ToString().ToLowerInvariant()), L(session.Player, "owner-" + a.Owner.ToString().ToLowerInvariant()), a.Provider)),
                .. this.diagnostics.IntegrationLines]
        };
    }

    private string[] PanelStatusLines(Session s) =>
    [
        L(s.Player, "character-group-function"),
        L(s.Player, "character-panel-health", PanelNumber(s.Player, s.Player.Entity.WatchedAttributes.GetTreeAttribute("health")?.TryGetFloat("currenthealth"), "0.0"), PanelNumber(s.Player, s.Player.Entity.WatchedAttributes.GetTreeAttribute("health")?.TryGetFloat("maxhealth"), "0.0")),
        L(s.Player, "character-panel-stamina", Number(s.Framework.Stamina, "P0")),
        L(s.Player, "character-panel-fatigue", Number(s.Framework.Fatigue, "P0")),
        L(s.Player, "character-panel-pain", Number(s.Framework.Pain, "P0")),
        "", L(s.Player, "character-group-fluids"),
        .. PanelHydrationLines(s),
        .. InteractionLines(s),
        "", .. WaterLines(s),
        "", L(s.Player, "character-group-wounds"),
        .. PanelWoundLines(s)
    ];

    private string[] PanelTemperatureLines(Session s)
    {
        var wet = s.Player.Entity.WatchedAttributes.TryGetFloat("wetness");
        var env = s.Environment;
        return
        [
            L(s.Player, "character-panel-core-model", Number(s.State.CoreTemperatureC, "0.00")),
            L(s.Player, "character-panel-skin-model", Number(s.State.SkinTemperatureC, "0.00")),
            L(s.Player, "character-panel-feels-like", PanelNumber(s.Player, s.Environment is { } boundary ? s.Kernel.EquivalentTemperatureC(boundary) : null, "0.0")),
            L(s.Player, "character-panel-radiant-temperature", PanelNumber(s.Player, s.Environment?.RadiantTemperatureC, "0.0")),
            L(s.Player, "character-panel-wetness", PanelNumber(s.Player, wet, "P0")),
            L(s.Player, "character-panel-environment", PanelNumber(s.Player, env?.AirTemperatureC, "0.0"), PanelNumber(s.Player, env?.RelativeHumidity, "P0"), PanelNumber(s.Player, env?.WindSpeedMps, "0.0")),
            L(s.Player, "character-panel-temperature-source")
        ];
    }

    private string[] PanelRecoveryLines(Session s) =>
    [
        L(s.Player, "character-panel-stamina", Number(s.Framework.Stamina, "P0")),
        L(s.Player, "character-panel-fatigue", Number(s.Framework.Fatigue, "P0")),
        L(s.Player, "character-panel-sleep-pressure", Number(s.State.SleepPressure, "P0")),
        L(s.Player, "character-panel-rest-state", L(s.Player, s.Player.Entity.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0
            ? "rest-sleeping" : s.ClosedEyeRest != ClosedEyeRestMode.None ? "rest-closed-eye"
            : IsSeatedRest(s) ? "rest-seated" : "rest-standing")),
        L(s.Player, s.State.SleepPressure > .7 ? "rest-need-sleep" : s.Framework.Fatigue > .6 ? "rest-need-seat" : "rest-normal"),
        L(s.Player, "character-panel-rest-hint"),
        L(s.Player, "character-panel-sleep-state", s.SleepObservationQuality),
        L(s.Player, "character-panel-diseases", s.Framework.Diseases.Length),
        L(s.Player, "character-panel-chronic", s.Framework.ChronicEffects.Length),
        L(s.Player, "character-panel-recovery-source")
    ];

    private static string L(IServerPlayer? player, string key, params object[] args) => player is null
        ? LocalizedText.Get("vsslssmod:" + key, args)
        : LocalizedText.GetL(player.LanguageCode, "vsslssmod:" + key, args);

    private string ModeName(IServerPlayer? player) => L(player, settings.Config.Mode == "survival" ? "mode-survival" : settings.Config.Mode == "shadow" ? "server-mode-shadow" : "server-mode-observe");

    private static string Number(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    private void SetFault(Session session, SessionFaultKind kind, object? detail)
    {
        session.Fault = kind;
        session.ClinicalWarnings.Reset(); session.PendingInsectBites = 0;
        CancelWash(session);
        RemoveEffects(session);
        // A failed adapter can be retried by later callbacks. Log each bounded category once per session.
        if (session.LoggedFaults.Add(kind))
            api.Logger.Error("[vsslssmod] Session {0}, fault={1}: {2}", session.Player.PlayerUID, kind, detail ?? string.Empty);
    }

    private static string FaultMessage(IServerPlayer? player, SessionFaultKind kind) => L(player, kind switch
    {
        SessionFaultKind.ConfigurationRejected => "server-startup-fault",
        SessionFaultKind.SaveFormatUnsupported => "server-fault-save-format",
        SessionFaultKind.SaveInvalid => "server-fault-save-invalid",
        SessionFaultKind.TimeStepRejected => "server-fault-time-step",
        SessionFaultKind.PhysiologyStepRejected => "server-fault-physiology-step",
        _ => "server-fault-session"
    });

    // Keep game localization at the adapter boundary; Application validation remains independent of the game API.
    private string ValidationMessage(IServerPlayer? player, string error)
    {
        var key = error switch
        {
            "Administrator permission required." => "server-permission-required",
            "Settings changed. Refresh and retry." => "server-settings-conflict",
            "ConfigVersion must be 1 or 2." => "server-invalid-config-version",
            "Mode must be survival, shadow or observe." => "mode-invalid",
            "PhysiologySpeed must be finite and between 0.1 and 20." => "server-invalid-speed",
            "CheckpointSeconds must be between 10 and 300." => "server-invalid-checkpoint-interval",
            _ => null
        };
        if (key is not null) return L(player, key);
        api.Logger.Warning("[vsslssmod] Unmapped settings validation failure: {0}", error);
        return L(player, "server-validation-failed");
    }

    private void SendSnapshot(Session session)
    {
        if (session.HelloAccepted) channel.SendPacket(Snapshot(session), session.Player);
    }

    private bool Save(Session session)
    {
        if (session.Fault is not null) return false;
        try
        {
            var save = new ShadowSave
            {
                WorldId = WorldId, PlayerUid = session.Player.PlayerUID, LifeGeneration = session.LifeGeneration,
                CheckpointSequence = session.CheckpointSequence + 1, SavedAtUtc = DateTime.UtcNow,
                Deceased = session.Deceased, Profile = session.Profile, State = session.State, Framework = session.Framework,
                Interactions = session.Interactions
            };
            var bytes = ShadowSaveCodec.Encode(save);
            var previous = session.Player.GetModdata(StateKey);
            if (previous is not null && ShadowSaveCodec.Decode(previous, WorldId, session.Player.PlayerUID).Success)
                session.Player.SetModdata(BackupKey, previous);
            session.Player.SetModdata(StateKey, bytes);
            session.CheckpointSequence = save.CheckpointSequence;
            return true;
        }
        catch (Exception ex)
        {
            api.Logger.Error("[vsslssmod] Checkpoint failed for {0}: {1}", session.Player.PlayerUID, ex);
            return false;
        }
    }

    private bool SaveAll()
    {
        var succeeded = true;
        foreach (var session in sessions.Values)
            if (!Save(session)) succeeded = false;
        return succeeded;
    }

    private void OnWorldSave() { using var timing = saveProbe?.Measure(api); SaveAll(); groundwater?.Save(); Pollution?.Save(); diagnostics.WritePerformance(); }

    public void Dispose()
    {
        if (disposed) return;
        SaveAll();
        smoke?.Dispose();
        gases?.Dispose();
        waterClassifier?.Dispose();
        groundwater?.Dispose();
        prospecting?.Dispose();
        Pollution?.Dispose();
        disposed = true;
        performance?.Dispose();
        api.Event.UnregisterGameTickListener(performanceListener);
        api.Event.UnregisterGameTickListener(listener);
        api.Event.PlayerReady -= OnReady;
        api.Event.DidPlaceBlock -= OnWaterBlockPlaced;
        api.Event.DidBreakBlock -= OnWaterBlockBroken;
        api.Event.BreakBlock -= CheckInjuryBlockBreak;
        api.Event.PlayerDisconnect -= OnDisconnect;
        api.Event.PlayerDeath -= OnDeath;
        api.Event.PlayerRespawn -= OnRespawn;
        api.Event.GameWorldSave -= OnWorldSave;
        api.Event.ServerSuspend -= OnSuspend;
        api.Event.ServerResume -= OnResume;
        foreach (var session in sessions.Values) CleanupSession(session);
        diagnostics.Dispose();
        sessions.Clear();
        groundwaterRevealCooldowns.Clear();
    }
}
