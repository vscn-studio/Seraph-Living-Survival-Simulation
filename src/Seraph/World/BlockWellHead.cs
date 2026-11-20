using Seraph.Core.Framework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using System.Collections.Generic;

namespace Seraph.World;

/// <summary>Routes right-clicks to the well shaft reservoir while preserving vanilla block behaviour.</summary>
public sealed class BlockWellHead : Block
{
    private WorldInteraction[]? lowerHelp, raiseHelp, takeHelp;

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode)) return false;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityWellHead well)
        { well.Direction = FixtureOrientation.Facing(byPlayer); well.MarkDirty(true); }
        return true;
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        if (lowerHelp is null)
        {
            var buckets = world.Blocks.Where(b => b is BlockLiquidContainerBase && b.Code?.Domain == "game"
                && b.Code.Path.StartsWith("woodbucket", StringComparison.Ordinal)).Select(b => new ItemStack(b)).ToArray();
            lowerHelp = [new() { MouseButton = EnumMouseButton.Right, ActionLangCode = "vsslssmod:help-well-lower", Itemstacks = buckets }];
            raiseHelp = [new() { MouseButton = EnumMouseButton.Right, HotKeyCode = "shift", ActionLangCode = "vsslssmod:help-well-raise" }];
            takeHelp = [new() { MouseButton = EnumMouseButton.Right, ActionLangCode = "vsslssmod:help-well-take" }];
        }
        if (world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityWellHead well || !well.WellComplete) return [];
        return well.BucketRaised ? takeHelp! : well.BucketInWell ? raiseHelp! : lowerHelp;
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        lowerHelp = raiseHelp = takeHelp = null;
        base.OnUnloaded(api);
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (blockSel is not null && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityWellHead well)
            return well.OnInteract(byPlayer);
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        => blockSel is not null && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityWellHead well
            ? well.OnInteractStep(byPlayer) : false;

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (blockSel is not null && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityWellHead well)
            well.OnInteractStop(byPlayer);
    }

    public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer,
        BlockSelection blockSel, EnumItemUseCancelReason cancelReason)
    {
        if (blockSel is not null && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityWellHead well)
            return well.OnInteractCancel(byPlayer);
        return true;
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var baseText = base.GetPlacedBlockInfo(world, pos, forPlayer);
        return world.BlockAccessor.GetBlockEntity(pos) is BlockEntityWellHead well
            ? baseText + (string.IsNullOrWhiteSpace(baseText) ? "" : "\n") + well.InfoText(forPlayer)
            : baseText;
    }
}

public sealed class BlockEntityWellHead : BlockEntity
{
    private bool releaseFormatRejected;

    private float fillFraction;
    private double? lastRefreshHours;
    private bool wellComplete;
    private int wellDepth;
    private double storageL;
    private double capacityL;
    private double flowLPerDay;
    private double waterTemperature, waterHazard, waterSalt, waterPollution, waterChemical;
    private WaterBatch storedWater = new() { VolumeL = 0, Source = "shallowwell", OriginSource = "shallowwell" };
    private IReadOnlyDictionary<string, double> waterAgents = PathogenLoads.Empty;
    private bool qualityKnown;
    private string pollutionSources = "";
    private ItemStack? bucketStack;
    private bool bucketInWell;
    private bool bucketRaised;
    private bool bucketRaiseActive;
    private float bucketRaiseProgress;
    private long serverListener;
    private MeshData? hangingBucketMesh;
    private MeshData? hangingRopeMesh;
    private MeshData? orientedMesh;
    private int meshDirection = -1;
    internal int Direction { get; set; }

    public float FillFraction => fillFraction;
    internal bool WellComplete => wellComplete;
    internal int WellDepth => wellDepth;
    internal double StorageL => storageL;
    internal WaterBatch StoredWater => storedWater with { VolumeL = storageL };
    internal double CapacityL => capacityL;
    internal double FlowLPerDay => flowLPerDay;
    internal bool BucketInWell => bucketInWell;
    internal bool BucketRaised => bucketRaised;
    internal ItemStack? BucketStack => bucketStack;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
        {
            serverListener = RegisterGameTickListener(_ => RefreshServerState(), 2000);
            RefreshServerState();
        }
        else if (api is ICoreClientAPI capi)
        {
            RebuildHangingMeshes(capi);
        }
    }

    private void RefreshServerState()
    {
        if (Api is null) return;
        var owner = Api.ModLoader.GetModSystem<Seraph.SeraphModSystem>().Server;
        wellComplete = WellStructureResolver.TryFromHead(Api.World.BlockAccessor, Pos, out var bounds);
        var state = wellComplete ? owner?.QueryWellState(bounds.Bottom) : null;
        var load = wellComplete ? owner?.Pollution?.LoadAt(bounds.Bottom) ?? default : default;
        var pollution = wellComplete ? Math.Max(PollutionModel.Levels(load).Water,
            WaterQuality.RegionAt(Api.World, bounds.Bottom).GroundPathogens) : 0;
        var sourceKind = wellComplete && WellStructureResolver.ProtectedIntake(Api.World.BlockAccessor, bounds)
            ? "protectedwell" : "shallowwell";
        var quality = state is not { } supply ? null : WaterQuality.ApplyPollution(Api.World, bounds.Bottom,
            SurfaceWater.Apply(new WaterBatch { VolumeL = 0, TemperatureC = supply.TemperatureC,
                SaltGramsPerL = supply.SalinityGPerL }, sourceKind, owner!.InteractionConfig.SurfaceWater),
            owner.InteractionConfig, groundwater: true);
        var known = wellComplete && owner?.Pollution?.Get(bounds.Bottom) is not null;
        var sources = wellComplete && owner?.Pollution?.Report(bounds.Bottom) is { } report
            ? System.Text.Json.JsonSerializer.Serialize(report with { At = 0 }) : "";
        var now = WorldCalendarTime.Hours(Api.World);
        var elapsedDays = lastRefreshHours is { } previous
            ? Math.Clamp(now - previous, 0, .25) / 24 : 0;
        lastRefreshHours = lastRefreshHours is { } cursor ? cursor + Math.Clamp(now - cursor, 0, .25) : now;
        var nextCapacity = state is { } aquifer
            ? WellReservoir.Capacity(bounds.BottomY, bounds.Depth,
                Api.World.BlockAccessor.GetTerrainMapheightAt(bounds.Bottom), aquifer.WaterTableDepthM) : 0;
        var previousStorage = storageL;
        storageL = Math.Min(storageL, nextCapacity);
        storedWater = storedWater with { VolumeL = storageL };
        var nextFlow = state is { } supplyState ? WellReservoir.InflowPerDay(nextCapacity, storageL, supplyState.Material) : 0;
        var incoming = WellReservoir.Incoming(nextCapacity, storageL, nextFlow, elapsedDays, state?.StorageL ?? 0);
        if (incoming > 0 && quality is not null && owner?.TryRefillWell(bounds.Bottom, incoming) == true)
        {
            storedWater = storedWater.Mix(quality with { VolumeL = incoming });
            storageL = storedWater.VolumeL;
        }
        var next = wellComplete ? WellReservoir.Height(storageL) / bounds.Depth : 0;
        var changed = Math.Abs(next - fillFraction) >= 0.005
            || wellDepth != (wellComplete ? bounds.Depth : 0)
            || Math.Abs(storageL - previousStorage) > .00001
            || Math.Abs(capacityL - nextCapacity) >= 0.1
            || qualityKnown != known || Math.Abs(waterHazard - storedWater.EntericHazardPerL) > .0001
            || waterChemical != 0
            || Math.Abs(waterTemperature - storedWater.TemperatureC) > .1
            || Math.Abs(waterSalt - storedWater.SaltGramsPerL) > .001
            || Math.Abs(waterPollution - pollution) > .001 || pollutionSources != sources
            || Math.Abs(flowLPerDay - nextFlow) > .1;
        pollutionSources = sources;
        qualityKnown = known; waterPollution = pollution;
        waterAgents = storedWater.PathogensPerL;
        waterChemical = 0;
        waterTemperature = storedWater.TemperatureC; waterHazard = storedWater.EntericHazardPerL; waterSalt = storedWater.SaltGramsPerL;
        fillFraction = (float)next;
        wellDepth = wellComplete ? bounds.Depth : 0;
        capacityL = nextCapacity;
        flowLPerDay = nextFlow;
        if (!changed && bucketStack is null) return;
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    internal bool TryDraw(double litres)
    {
        if (!wellComplete || !double.IsFinite(litres) || litres <= 0 || litres > storageL) return false;
        storageL -= litres;
        storedWater = storedWater with { VolumeL = storageL };
        fillFraction = wellDepth > 0 ? (float)(WellReservoir.Height(storageL) / wellDepth) : 0;
        MarkDirty(true);
        return true;
    }

    public bool OnInteract(IPlayer player)
    {
        if (Api?.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return true;
        var owner = Api.ModLoader.GetModSystem<Seraph.SeraphModSystem>().Server;
        if (owner is null) return true;
        if (serverPlayer.Entity?.Controls.ShiftKey == true)
            return owner.TryBeginWellBucketRaise(serverPlayer, this);
        if (bucketRaised)
            return owner.TryTakeWellBucket(serverPlayer, this);
        if (bucketInWell)
        {
            return true;
        }
        return owner.TryLowerWellBucket(serverPlayer, this);
    }

    internal bool OnInteractStep(IPlayer player)
        => player.Entity?.Controls.ShiftKey == true
            && (Api?.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer
                || Api.ModLoader.GetModSystem<Seraph.SeraphModSystem>().Server?.IsWellBucketRaiseActive(serverPlayer, this) == true);

    internal void OnInteractStop(IPlayer player)
    {
        if (Api?.Side == EnumAppSide.Server && player is IServerPlayer serverPlayer)
            Api.ModLoader.GetModSystem<Seraph.SeraphModSystem>().Server?.StopWellBucketRaise(serverPlayer, this);
    }

    internal bool OnInteractCancel(IPlayer player)
    {
        OnInteractStop(player);
        return true;
    }

    internal void SetBucket(ItemStack stack, bool inWell, bool raised)
    {
        bucketStack = stack;
        bucketInWell = inWell;
        bucketRaised = raised;
        bucketRaiseActive = false;
        bucketRaiseProgress = raised ? 1f : 0f;
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    internal ItemStack? RemoveBucket()
    {
        var result = bucketStack;
        bucketStack = null;
        bucketInWell = false;
        bucketRaised = false;
        bucketRaiseActive = false;
        bucketRaiseProgress = 0f;
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
        return result;
    }

    internal void SetRaised(bool raised)
    {
        bucketRaised = raised;
        bucketRaiseActive = false;
        bucketRaiseProgress = raised ? 1f : 0f;
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    internal void SetRaiseProgress(double progress)
    {
        if (!bucketInWell || bucketRaised) return;
        var next = Math.Clamp((float)progress, 0, 1);
        if (bucketRaiseActive && Math.Abs(next - bucketRaiseProgress) < 0.035f) return;
        bucketRaiseActive = true;
        bucketRaiseProgress = next;
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    internal void ClearRaiseProgress()
    {
        if (!bucketRaiseActive && bucketRaiseProgress == 0) return;
        bucketRaiseActive = false;
        bucketRaiseProgress = 0;
        MarkDirty(true);
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    public string InfoText(IPlayer player)
    {
        var bucket = bucketInWell ? Lang.Get(bucketRaised ? "vsslssmod:well-bucket-raised" : "vsslssmod:well-bucket-in-well") : Lang.Get("vsslssmod:well-bucket-none");
        if (!wellComplete) return Lang.Get("vsslssmod:wellhead-incomplete");
        var lines = new List<string> { Lang.Get("vsslssmod:wellhead-litres", storageL.ToString("0.0"), capacityL.ToString("0.0")),
            Lang.Get("vsslssmod:wellhead-depth", wellDepth),
            Lang.Get("vsslssmod:wellhead-flow", (flowLPerDay / 24).ToString("0.0")),
            Lang.Get("vsslssmod:water-temperature", waterTemperature.ToString("0.0")),
            Lang.Get("vsslssmod:water-salt", waterSalt.ToString("0.00")), bucket };
        lines.AddRange(PollutionText.Water(StoredWater,
            (key, args) => LocalizedText.Get("vsslssmod:" + key, args), includeWaste: false));
        if (pollutionSources.Length > 0)
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<PollutionSnapshot>(pollutionSources) is { } report)
                    lines.Add(Lang.Get("vsslssmod:pollution-sources", PollutionText.Sources(report)));
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (!qualityKnown) lines.Add(Lang.Get("vsslssmod:pollution-pending"));
        return string.Join("\n", lines);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        if (releaseFormatRejected) throw new InvalidDataException("Rejected release block state cannot be saved.");
        base.ToTreeAttributes(tree);
        tree.SetString("seraphFormat", "seraph.release.well");
        tree.SetInt("seraphVersion", 1);
        tree.SetInt("direction", Direction);
        tree.SetFloat("fillFraction", fillFraction);
        tree.SetBool("wellComplete", wellComplete);
        tree.SetInt("wellDepth", wellDepth);
        tree.SetInt("wellReservoirSchema", 1);
        tree.SetDouble("storageL", storageL);
        tree.SetString("storedWater", System.Text.Json.JsonSerializer.Serialize(StoredWater));
        tree.SetDouble("capacityL", capacityL);
        tree.SetDouble("flowLPerDay", flowLPerDay);
        tree.SetString("waterAgents", System.Text.Json.JsonSerializer.Serialize(waterAgents));
        tree.SetDouble("waterTemperature", waterTemperature); tree.SetDouble("waterHazard", waterHazard);
        tree.SetDouble("waterChemical", waterChemical);
        tree.SetDouble("waterSalt", waterSalt); tree.SetDouble("waterPollution", waterPollution); tree.SetBool("qualityKnown", qualityKnown);
        tree.SetString("pollutionSources", pollutionSources);
        tree.SetBool("bucketInWell", bucketInWell);
        tree.SetBool("bucketRaised", bucketRaised);
        tree.SetBool("bucketRaiseActive", bucketRaiseActive);
        tree.SetFloat("bucketRaiseProgress", bucketRaiseProgress);
        if (bucketStack is not null) tree.SetString("bucketStack", Convert.ToBase64String(bucketStack.ToBytes()));
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        if ((tree.HasAttribute("storageL") || tree.HasAttribute("direction") || tree.HasAttribute("bucketStack")) && (tree.GetString("seraphFormat") != "seraph.release.well" || tree.GetInt("seraphVersion") != 1))
        { releaseFormatRejected = true; throw new InvalidDataException("Unsupported release well format; original state preserved."); }
        base.FromTreeAttributes(tree, worldAccessForResolve);
        try
        {
            double Number(string key, double fallback, double min, double max)
            {
                var value = tree.GetDouble(key, fallback);
                if (!double.IsFinite(value) || value < min || value > max)
                    throw new InvalidDataException("Invalid release well value: " + key);
                return value;
            }
            float Fraction(string key, float fallback = 0)
            {
                var value = tree.GetFloat(key, fallback);
                if (!float.IsFinite(value) || value < 0 || value > 1)
                    throw new InvalidDataException("Invalid release well fraction: " + key);
                return value;
            }
            Direction = tree.GetInt("direction", 0);
            wellDepth = tree.GetInt("wellDepth", 0);
            if (Direction is < 0 or > 3 || wellDepth < 0 || wellDepth > WellStructureResolver.MaxDepth)
                throw new InvalidDataException("Invalid release well geometry.");
            fillFraction = Fraction("fillFraction");
            wellComplete = tree.GetBool("wellComplete", false);
            var maximum = WellStructureResolver.MaxDepth * WellReservoir.AreaM2 * 1000;
            storageL = Number("storageL", 0, 0, maximum);
            capacityL = Number("capacityL", 0, 0, maximum);
            flowLPerDay = Number("flowLPerDay", 0, 0, double.MaxValue);
            var json = tree.GetString("waterAgents", "{}");
            if (json.Length > 4096) throw new InvalidDataException("Oversized well pathogen data.");
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (!PathogenLoads.Valid(parsed)) throw new InvalidDataException("Invalid well pathogens.");
            waterAgents = parsed!;
            waterTemperature = Number("waterTemperature", 15, -273.15, 10000);
            waterHazard = Number("waterHazard", 0, 0, double.MaxValue);
            waterChemical = Number("waterChemical", 0, 0, 1);
            waterSalt = Number("waterSalt", 0, 0, double.MaxValue);
            waterPollution = Number("waterPollution", 0, 0, 1);
            var batchJson = tree.GetString("storedWater", "");
            if (batchJson.Length > 8192) throw new InvalidDataException("Oversized well water batch.");
            if (batchJson.Length > 0)
            {
                using var document = System.Text.Json.JsonDocument.Parse(batchJson);
                storedWater = WaterQualityCompatibility.Read(document.RootElement)!;
                if (Math.Abs(storedWater.VolumeL - storageL) > .0001)
                    throw new InvalidDataException("Well volume and batch disagree.");
            }
            else
            {
                // Legacy wells had no turbidity field. Preserve all saved bio-load,
                // salt and temperature; migration does not refill or purify water.
                storedWater = new WaterBatch { VolumeL = storageL, TemperatureC = waterTemperature,
                    EntericHazardPerL = waterHazard, PathogensPerL = waterAgents,
                    SaltGramsPerL = waterSalt, Turbidity = .15, Source = "groundwater", OriginSource = "groundwater" };
                if (storedWater.Validate() is not null) throw new InvalidDataException("Invalid legacy well water.");
            }
            qualityKnown = tree.GetBool("qualityKnown");
            pollutionSources = tree.GetString("pollutionSources", "");
            bucketInWell = tree.GetBool("bucketInWell", false);
            bucketRaised = tree.GetBool("bucketRaised", false);
            bucketRaiseActive = tree.GetBool("bucketRaiseActive", false);
            bucketRaiseProgress = Fraction("bucketRaiseProgress", bucketRaised ? 1f : 0f);
            bucketStack = null;
            var encoded = tree.GetString("bucketStack", "");
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                if (encoded.Length > 1048576) throw new InvalidDataException("Oversized well bucket data.");
                bucketStack = new ItemStack(Convert.FromBase64String(encoded));
                if (!bucketStack.ResolveBlockOrItem(worldAccessForResolve))
                    throw new InvalidDataException("Unresolvable well bucket.");
            }
            if (bucketInWell && bucketStack is null)
                throw new InvalidDataException("Missing retained well bucket.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            releaseFormatRejected = true;
            throw new InvalidDataException("Rejected release well state; original data preserved.", error);
        }
        if (Api is ICoreClientAPI capi)
        {
            RebuildHangingMeshes(capi);
            // The tree update arrives outside the normal block placement path;
            // explicitly request a client redraw for the new bucket state.
            MarkDirty(true);
        }
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    public override void OnBlockRemoved()
    {
        if (serverListener != 0) UnregisterGameTickListener(serverListener);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        if (serverListener != 0) UnregisterGameTickListener(serverListener);
        base.OnBlockUnloaded();
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        if (Api is not ICoreClientAPI capi) return false;

        // BlockEntity.OnTesselation replaces the block's normal mesh when it
        // returns true. Keep the well head visible, then append the temporary
        // bucket and rope meshes.
        if (orientedMesh is null || meshDirection != Direction)
        {
            var baseMesh = capi.TesselatorManager.GetDefaultBlockMesh(Block);
            if (baseMesh is not { VerticesCount: > 0 }) return false;
            orientedMesh = FixtureOrientation.Mesh(baseMesh, Direction);
            meshDirection = Direction;
        }
        mesher.AddMeshData(orientedMesh);

        if (!bucketInWell) return true;

        RebuildHangingMeshes(capi);
        if (hangingRopeMesh is { VerticesCount: > 0 })
            mesher.AddMeshData(FixtureOrientation.Mesh(hangingRopeMesh, Direction));
        if (hangingBucketMesh is { VerticesCount: > 0 })
            mesher.AddMeshData(FixtureOrientation.Mesh(hangingBucketMesh, Direction));

        return true;
    }

    private void RebuildHangingMeshes(ICoreClientAPI capi)
    {
        hangingBucketMesh = null;
        hangingRopeMesh = null;
        if (!bucketInWell) return;

        // Reuse the vanilla BlockBucket mesh generator so the bucket geometry and
        // wood texture stay identical to the survival game's placed bucket.
        if (capi.World.GetBlock(new AssetLocation("game:woodbucket")) is BlockBucket bucketBlock)
        {
            var content = bucketStack is null ? null : bucketBlock.GetContent(bucketStack);
            var mesh = bucketBlock.GenMesh(capi, content, Pos);
            if (mesh is { VerticesCount: > 0 })
            {
                // These flags are used by the vanilla bucket block entity to
                // keep the carried mesh in the correct terrain render path.
                if (mesh.CustomInts is not null)
                {
                    for (var i = 0; i < mesh.CustomInts.Count; i++)
                    {
                        mesh.CustomInts.Values[i] |= 536870912;
                        mesh.CustomInts.Values[i] |= 268435456;
                    }
                }
                mesh.WithRenderpasses();
                // Lower the bucket to the virtual water surface. During a
                // pull action the synced progress moves it back to the beam.
                var depth = Math.Max(1, wellDepth);
                var waterSurface = -depth + fillFraction * depth;
                var loweredY = waterSurface + 0.02f;
                var raisedY = -0.36f;
                var motion = bucketRaised || bucketRaiseActive ? bucketRaiseProgress : 0f;
                var bucketY = loweredY + (raisedY - loweredY) * Math.Clamp(motion, 0, 1);
                hangingBucketMesh = mesh.Translate(0, bucketY, 0);
            }
        }

        var rope = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/well-rope.json"));
        if (rope?.Elements is { Length: > 0 })
        {
            var ropeTextures = new Dictionary<string, AssetLocation>(StringComparer.Ordinal)
            {
                ["rope"] = new("game:block/cloth/burlap/string")
            };
            var ropeSource = new ContainedTextureSource(capi, capi.BlockTextureAtlas, ropeTextures, "rope");
            capi.Tesselator.TesselateShape("vsslssmod-well-rope", rope, out var mesh,
                ropeSource, null, 0, 0, 0, null, null);
            if (mesh is { VerticesCount: > 0 })
            {
                mesh.WithRenderpasses();
                var depth = Math.Max(1, wellDepth);
                var waterSurface = -depth + fillFraction * depth;
                var loweredY = waterSurface + 0.02f;
                var raisedY = -0.36f;
                var motion = bucketRaised || bucketRaiseActive ? bucketRaiseProgress : 0f;
                var bucketY = loweredY + (raisedY - loweredY) * Math.Clamp(motion, 0, 1);

                // Keep the rope length calculation unchanged, but move both
                // endpoints down so the top sits inside the beam and the lower
                // endpoint meets the bucket handle.
                const float ropeVerticalOffset = 0f;
                var ropeBottom = bucketY + 0.82f + ropeVerticalOffset;
                var ropeTop = 25f / 16f;
                var ropeLength = Math.Max(0.05f, ropeTop - ropeBottom);

                // A single scaled mesh stretches the string texture over the
                // entire well. Repeat the one-block rope mesh instead; only a
                // final partial segment is scaled for the remaining distance.
                MeshData? ropeCombined = null;
                var remaining = ropeLength;
                var segmentBottom = ropeBottom;
                while (remaining > 0.0001f)
                {
                    var segmentLength = Math.Min(1f, remaining);
                    var segment = mesh.Clone();
                    segment.Scale(new Vec3f(0.5f, 0f, 0.5f), 1f, segmentLength, 1f);
                    segment.Translate(0, segmentBottom, 0);
                    if (ropeCombined is null) ropeCombined = segment;
                    else ropeCombined.AddMeshData(segment);
                    segmentBottom += segmentLength;
                    remaining -= segmentLength;
                }
                hangingRopeMesh = ropeCombined;
            }
        }

        var coil = Shape.TryGet(capi, new AssetLocation("vsslssmod:shapes/block/well-rope-coil"));
        if (coil?.Elements is { Length: > 0 })
        {
            var coilTextures = new Dictionary<string, AssetLocation>(StringComparer.Ordinal)
            {
                ["rope"] = new("game:block/cloth/burlap/string")
            };
            var coilSource = new ContainedTextureSource(capi, capi.BlockTextureAtlas, coilTextures, "rope");
            capi.Tesselator.TesselateShape("vsslssmod-well-rope-coil", coil, out var coilMesh,
                coilSource, null, 0, 0, 0, null, null);
            if (coilMesh is { VerticesCount: > 0 })
            {
                coilMesh.WithRenderpasses();
                if (hangingRopeMesh is null) hangingRopeMesh = coilMesh;
                else hangingRopeMesh.AddMeshData(coilMesh);
            }
        }
    }
}
