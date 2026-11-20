using Seraph.Application;
using Seraph.Client;
using Seraph.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Seraph;

public sealed partial class SeraphModSystem : ModSystem
{
    private ServerCoordinator? server;
    private ICoreAPI? coreApi;
    private World.NativeWaterTransfer? waterTransfer;
    private World.RecipeMetadataCompatibility? recipeMetadata;
    private World.NativeWaterHeating? waterHeating;
    private World.NativeWaterDistillation? waterDistillation;
    private NativePhysiologyBridge? physiologyBridge;
    private NativeCalendarBridge? calendarBridge;
    private NativeFoodBridge? foodBridge;
    private NativeSanitationBridge? sanitationBridge;
    private World.NativeCropPollution? cropPollution;
    private World.PollutionChangeBridge? pollutionChanges;
    private World.PathogenStackMerging? pathogenStackMerging;
    private World.NativeManureFertilizer? manureFertilizer;
    private World.NativeAshGeneration? ashGeneration;
    private World.NativeStoneFirepit? stoneFirepit;
    private NativeBandageBridge? bandageBridge;
    private NativeDownedBridge? downedBridge;
    private BleedingVisualBridge? bleedingVisualBridge;
    private NativeInjuryActionBridge? injuryActionBridge;
    private NativeLaborBridge? laborBridge;
    private NativeVehicleBridge? vehicleBridge;
    internal ServerCoordinator? Server => server;
    internal SeraphServerConfig InteractionConfig => server?.InteractionConfig ?? new() { Mode = "observe" };
    public SeraphClientBridge? ClientBridge { get; private set; }

    /// <summary>Optional client entry point used by the civilization core menu.</summary>
    public void OpenNutritionHandbook() => ClientBridge?.OpenNutritionHandbook();

    public override void Start(ICoreAPI api)
    {
        coreApi = api;
        LocalizedText.Logger = api.Logger;
        api.RegisterItemClass("vsslssmodmanure", typeof(World.ItemManure));
        api.RegisterEntityBehaviorClass("vsslssmodclosedeyerest", typeof(ClosedEyeRestBehavior));
        api.RegisterBlockEntityBehaviorClass("vsslssmodsaltresidue", typeof(World.SaltResidueBehavior));
        api.ModLoader.GetModSystem<Vintagestory.GameContent.WorldMapManager>()
            .RegisterMapLayer<World.PollutionMapLayer>("vsslssmodpollution", .2);
        api.RegisterBlockClass("vsslssmodwellhead", typeof(World.BlockWellHead));
        api.RegisterBlockEntityClass("vsslssmodchimney", typeof(World.BlockEntityChimney));
        api.RegisterBlockClass("vsslssmodchimney", typeof(World.BlockChimney));
        api.RegisterBlockClass("vsslssmodchimneypart", typeof(World.BlockChimneyPart));
        api.RegisterBlockClass("vsslssmodbathtub", typeof(World.BlockBathtub));
        api.RegisterBlockClass("vsslssmodlatrine", typeof(World.BlockLatrine));
        api.RegisterBlockClass("vsslssmodwatertreatment", typeof(World.BlockWaterTreatment));
        api.RegisterBlockEntityClass("vsslssmodwatertreatment", typeof(World.BlockEntityWaterTreatment));
        api.RegisterBlockEntityClass("vsslssmodwellhead", typeof(World.BlockEntityWellHead));
        api.RegisterBlockEntityClass("vsslssmodwellwall", typeof(World.BlockEntityWellWall));
        api.RegisterBlockEntityClass("vsslssmodbathtub", typeof(World.BlockEntityBathtub));
        api.RegisterBlockEntityClass("vsslssmodlatrine", typeof(World.BlockEntityLatrine));
        api.RegisterMountable("vsslssmod-latrine", (world, tree) =>
            world.BlockAccessor.GetBlockEntity(new Vintagestory.API.MathTools.BlockPos(
                tree.GetInt("x"), tree.GetInt("y"), tree.GetInt("z"))) as World.BlockEntityLatrine);
        waterTransfer = new World.NativeWaterTransfer(api);
        recipeMetadata = new(api.Logger);
        waterHeating = new();
        waterDistillation = new();
        injuryActionBridge = new(api);
        laborBridge = new();
        vehicleBridge = new(api);
        manureFertilizer = new();
        ashGeneration = new();
        stoneFirepit = new();
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        FoodCalibration.Apply(api);
        vehicleBridge?.ResolveOptional();
        server?.ReportRuntimeCompatibility();
        BleedingVisualBridge.AttachParticles(api);
        waterTransfer?.RegisterBlocks();
        if (api.Side == EnumAppSide.Client)
            foreach (var collectible in api.World.Items.Cast<CollectibleObject>().Concat(api.World.Blocks))
            {
                if (collectible?.Code is null) continue;
                var behaviors = collectible.CollectibleBehaviors ?? [];
                if (!behaviors.Any(b => b is FoodInfoBehavior))
                    collectible.CollectibleBehaviors = [.. behaviors, new FoodInfoBehavior(collectible)];
            }
    }

    internal void DrinkFromContainer(IServerPlayer player, ItemSlot slot, Vintagestory.GameContent.BlockLiquidContainerBase container) =>
        server?.DrinkFromContainer(player, slot, container);

    public override void StartPre(ICoreAPI api)
    {
        // Reflection reads the loaded game's constant; a direct const access would inline the build version.
        var runtimeVersion = typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion))?.GetRawConstantValue() as string;
        if (!TargetVersion.IsSupported(runtimeVersion))
            throw new NotSupportedException($"vsslssmod 需要 Vintage Story 1.22.x；当前加载版本：{runtimeVersion ?? "未知"}。 / vsslssmod targets Vintage Story 1.22.x; loaded {runtimeVersion ?? "unknown"}.");
        api.Logger.Notification("[vsslssmod] {0} / HansJack / MIT / VS {1}. Release.", Seraph.Core.BuildInfo.Version, runtimeVersion);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        server = new ServerCoordinator(api);
        server.Start();
        downedBridge = new(api);
        physiologyBridge = new(api);
        calendarBridge = new(server);
        foodBridge = new(api);
        sanitationBridge = new(server);
        cropPollution = new();
        pollutionChanges = new();
        pathogenStackMerging = new();
        bandageBridge = new();
        bleedingVisualBridge = new(false, api);
        server.ReportRuntimeCompatibility();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        bleedingVisualBridge = new(true, api);
        ClientBridge = new SeraphClientBridge(api);
    }

    public override void Dispose()
    {
        ClientBridge?.Dispose();
        physiologyBridge?.Dispose();
        calendarBridge?.Dispose();
        foodBridge?.Dispose();
        sanitationBridge?.Dispose();
        cropPollution?.Dispose();
        pollutionChanges?.Dispose();
        pathogenStackMerging?.Dispose();
        manureFertilizer?.Dispose();
        ashGeneration?.Dispose();
        stoneFirepit?.Dispose();
        bandageBridge?.Dispose();
        downedBridge?.Dispose();
        bleedingVisualBridge?.Dispose();
        injuryActionBridge?.Dispose();
        laborBridge?.Dispose();
        vehicleBridge?.Dispose();
        server?.Dispose();
        waterTransfer?.Dispose();
        recipeMetadata?.Dispose();
        waterHeating?.Dispose();
        waterDistillation?.Dispose();
    }
}
