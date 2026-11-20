using System.Reflection;
using HarmonyLib;
using RealSmoke;
using RealSmoke.Smoke;
using RealSmoke.Smoke.Simulation;
using Seraph.Core.Framework;
using Seraph.World;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Independent gas tracers using Real Smoke's native collision, wind and parcel physics.
/// Tracer mass uses 100 units per game gas unit; composition never uses soot-content fields.</summary>
internal sealed class RealSmokeGasTransport : IGasTransport
{
    private const float Scale = 100;
    private static readonly List<RealSmokeGasTransport> Instances = [];
    private static readonly System.Func<SmokeParcelStore, int, SmokeParcelHandle> HandleAt = AccessTools.Method(typeof(SmokeParcelStore), "GetHandleAtDenseIndex")
        .CreateDelegate<System.Func<SmokeParcelStore, int, SmokeParcelHandle>>();
    private static SmokeParcelStore Store(SmokeParcelAuthority authority)
        => (SmokeParcelStore)AccessTools.Field(typeof(SmokeParcelAuthority), "store").GetValue(authority)!;
    private readonly ICoreServerAPI api;
    private readonly ChunkPollutionStore pollution;
    private readonly IServerNetworkChannel visualChannel;
    private readonly SmokeController native;
    private readonly SmokeParcelSimulator simulator;
    private readonly SmokeParcelAuthority[] channels = [new(1024), new(1024), new(1024), new(1024)];
    private readonly SmokeParcelStore[] stores;
    private readonly SmokeParcelStore soot;
    private readonly Harmony harmony = new("vsslssmod.gas-transport");
    private readonly List<SmokeParcelHandle> handles = [];
    private readonly List<SmokeParcelState> states = [];
    private readonly List<SmokeParcelHandle> created = [];
    private readonly int[] cursors = new int[5];
    private readonly Dictionary<(int Channel, SmokeParcelHandle Handle), long> contacts = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BlockEntity, FuelMemory> fuels = new();
    private sealed class FuelMemory { internal BurningFuel Value; }
    private long listener, step, lastOrganic, lastParticles, lastPrune;
    private int slice, particleBudget, organicCursor, mistCursor;
    private bool disposed;
    public bool Ready { get; private set; }

    internal RealSmokeGasTransport(ICoreServerAPI api, ChunkPollutionStore pollution, IServerNetworkChannel visualChannel)
    {
        this.api = api; this.pollution = pollution; this.visualChannel = visualChannel;
        var system = api.ModLoader.Systems.First(s => s.GetType().FullName == "RealSmoke.ModSystem");
        native = (SmokeController)system.GetType().GetProperty("SmokeController")!.GetValue(system)!;
        simulator = new(api);
        stores = channels.Select(Store).ToArray();
        soot = Store((SmokeParcelAuthority)AccessTools.Field(typeof(SmokeController), "parcelAuthority").GetValue(native)!);
        try
        {
            // Observe accepted emission volume, including emission merged into existing
            // soot. Hooking only TryEmitParcel misses this common native release path.
            harmony.Patch(AccessTools.Method(typeof(SmokeUtils), "TryVent"),
                prefix: new HarmonyMethod(typeof(RealSmokeGasTransport), nameof(BeforeVent)),
                postfix: new HarmonyMethod(typeof(RealSmokeGasTransport), nameof(AfterVent)));
            harmony.Patch(AccessTools.Method(typeof(SmokeUtils), "TryVentAtPoint"),
                prefix: new HarmonyMethod(typeof(RealSmokeGasTransport), nameof(BeforeVent)),
                postfix: new HarmonyMethod(typeof(RealSmokeGasTransport), nameof(AfterPoint)));
            harmony.Patch(AccessTools.Method(typeof(BlockEntityBehaviorRealGasPump), "PumpGas"),
                postfix: new HarmonyMethod(typeof(RealSmokeGasTransport), nameof(AfterPump)));
            Instances.Add(this); Ready = true;
            lastOrganic = api.World.ElapsedMilliseconds;
            listener = api.Event.RegisterGameTickListener(_ => Tick(), 20);
        }
        catch { Dispose(); throw; }
    }

    private static void BeforeVent(int releasableSmoke, out int __state) => __state = releasableSmoke;
    private static void AfterVent(SmokeController controller, BlockPos sourcePosition, BlockPos targetPosition,
        double solidTopBelowTarget, Vec3d emitFrom, int temperature, int releasableSmoke, int __state)
    {
        if (__state <= releasableSmoke || sourcePosition.dimension != 0) return;
        SmokeUtils.VentPosition(sourcePosition, targetPosition, solidTopBelowTarget, emitFrom, out var x, out var y, out var z);
        SmokeUtils.VentVelocity(sourcePosition, targetPosition, out var vx, out var vy, out var vz);
        foreach (var bridge in Instances)
            if (bridge.Ready && ReferenceEquals(bridge.native, controller) && controller.CapacityLimit > 0)
                try { bridge.EmitCombustion(new(x, y, z), new(vx, vy, vz), temperature, __state - releasableSmoke, sourcePosition); }
                catch (Exception ex) { bridge.Fail(ex); }
    }
    private static void AfterPoint(SmokeController controller, Vec3d point, FastVec3d velocity,
        int temperature, int releasableSmoke, int __state)
    {
        if (__state <= releasableSmoke) return;
        foreach (var bridge in Instances)
            if (bridge.Ready && ReferenceEquals(bridge.native, controller) && controller.CapacityLimit > 0)
                try { bridge.EmitCombustion(new(point.X, point.Y, point.Z), velocity, temperature, __state - releasableSmoke, null); }
                catch (Exception ex) { bridge.Fail(ex); }
    }
    private void EmitCombustion(FastVec3d position, FastVec3d velocity, float temperature, int accepted, BlockPos? source)
    {
        if (!Ready) return;
        var fuel = BurningFuel.Wood;
        if (source is not null && api.World.BlockAccessor.GetBlockEntity(source) is { } entity)
        {
            var memory = fuels.GetOrCreateValue(entity);
            var stack = entity is BlockEntityFirepit pit ? pit.fuelSlot.Itemstack
                : entity is BlockEntityForge forge ? forge.FuelSlot.Itemstack : null;
            if (stack?.Collectible.Code.Path is { } path) memory.Value = path.Contains("charcoal", StringComparison.Ordinal)
                ? BurningFuel.Charcoal : path.Contains("coal", StringComparison.Ordinal) || path.Contains("coke", StringComparison.Ordinal)
                ? BurningFuel.Coal : BurningFuel.Wood;
            fuel = memory.Value;
        }
        var signature = RegionalEnvironmentModel.Combustion(fuel);
        Emit(0, position, velocity, temperature, accepted * signature.CO);
        Emit(1, position, velocity, temperature, accepted * signature.SO2);
    }
    private void Emit(int channel, FastVec3d position, FastVec3d velocity, float temperature, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0 || !Ready) return;
        var mass = (float)Math.Min(6400, amount * Scale);
        var state = new SmokeParcelState(position, velocity, position, mass,
            SmokeThermodynamics.Energy(temperature, mass), 0, 0, 0, 0, (float)position.Y,
            (int)position.Y, false, false, temperature);
        // Native TryCreate evicts an existing parcel when full. Skip excess new
        // production instead of deleting unrelated gas to make room.
        if (channels[channel].Count < channels[channel].CapacityLimit) channels[channel].TryCreate(state, out _);
    }
    public AirChemistry Sample(Vec3d eye, int dimension)
    {
        if (!Ready || dimension != 0) return default;
        double Read(int channel)
        {
            channels[channel].CopyStatesInRange(eye, .5, .5, handles, states);
            return Math.Clamp(states.Sum(s => (double)s.Amount) / (Scale * 8), 0, 1);
        }
        return new(Read(0), 0, 0, Read(1), Read(2), 0, Read(3));
    }
    private void Tick()
    {
        if (!Ready || disposed) return;
        try
        {
            // Exactly one shared environment/cache slice for all gas species.
            // No calendar catch-up: sleep acceleration never multiplies emissions.
            simulator.BeginSlice(slice);
            foreach (var channel in channels)
            {
                created.Clear(); channel.AdvanceSlice(simulator, .1f, step, slice, created);
                if (slice == 2) channel.ProcessInteractions(simulator, step);
                if (slice == 4) channel.CompleteStep(simulator);
            }
            if (++slice == 5) { slice = 0; step++; }
            var now = api.World.ElapsedMilliseconds;
            if (now - lastOrganic >= 1000)
            {
                var dt = Math.Min(2, (now - lastOrganic) / 1000d); lastOrganic = now;
                // Snapshot only actual captured sources; sealed stores are absent.
                var sources = pollution.OrganicGasSources().ToArray();
                var count = Math.Min(1024, sources.Length);
                for (var i = 0; i < count; i++)
                {
                    var (pos, gas, _) = sources[(organicCursor + i) % sources.Length];
                    if (pos.dimension != 0 || api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null) continue;
                    var outlet = pos.UpCopy();
                    if (api.World.BlockAccessor.GetChunkAtBlockPos(outlet) is null) continue;
                    var blocked = api.World.BlockAccessor.GetBlock(outlet).SideSolid[BlockFacing.DOWN.Index];
                    // A container's own solid hull is not an emission barrier:
                    // containment was already evaluated from its physical sealed state.
                    var temperature = api.World.BlockAccessor.GetClimateAt(pos)?.Temperature ?? 15;
                    // A blocked outlet retains the gas in the source cell.
                    var p = new FastVec3d(pos.X + .5, pos.Y + (blocked ? .5 : 1.02), pos.Z + .5);
                    Emit(2, p, new(0, .04, 0), temperature, gas.NH3 * dt);
                    Emit(3, p, new(0, .01, 0), temperature, gas.H2S * dt);
                }
                organicCursor = sources.Length == 0 ? 0 : (organicCursor + count) % sources.Length;
                // Near-source cues do not depend on a rare transported-parcel draw.
                // Keep an independent bounded cursor so dense chunks cannot starve later sources.
                var mistCount = Math.Min(16, sources.Length);
                for (var i = 0; i < mistCount; i++)
                {
                    var (pos, gas, entityId) = sources[(mistCursor + i) % sources.Length];
                    if (pos.dimension != 0 || api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null
                        || api.World.BlockAccessor.GetChunkAtBlockPos(pos.UpCopy()) is null) continue;
                    if (gas.NH3 <= 0 && gas.H2S <= 0) continue;
                    // Use the source's actual selection shape, including partial-height piles.
                    var block = api.World.BlockAccessor.GetBlock(pos);
                    var boxes = block.GetSelectionBoxes(api.World.BlockAccessor, pos);
                    var top = boxes is { Length: > 0 } ? boxes.Max(b => b.Y2) : .15f;
                    var point = new Vec3d(pos.X + .3 + api.World.Rand.NextDouble() * .4,
                        pos.Y + Math.Clamp(top, .05f, 1f) + .02, pos.Z + .3 + api.World.Rand.NextDouble() * .4);
                    if (entityId != 0 && api.World.GetEntityById(entityId) is { } entity
                        && entity.Pos.Dimension == pos.dimension && entity.Pos.AsBlockPos.Equals(pos))
                        point.Set(entity.Pos.X, entity.Pos.Y + .15, entity.Pos.Z);
                    SpawnMist(point, new(0, .025f, 0));
                }
                mistCursor = sources.Length == 0 ? 0 : (mistCursor + mistCount) % sources.Length;
            }
            if (now - lastParticles >= 1000) { lastParticles = now; particleBudget = 4; }
            // Bounded actual mass sinks; round-robin visits preserve fairness.
            for (var channel = 0; channel < 5; channel++) ApplyVegetation(channel, now);
            if (now - lastPrune > 10000)
            { lastPrune = now; foreach (var key in contacts.Where(p => now - p.Value > 30000).Select(p => p.Key).ToArray()) contacts.Remove(key); }
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void Fail(Exception ex)
    {
        if (!Ready) return;
        Ready = false;
        api.Logger.Warning("[vsslssmod] Gas transport stopped; new gas exposure disabled: {0}", ex);
    }
    private void ApplyVegetation(int channel, long now)
    {
        var store = channel == 4 ? soot : stores[channel];
        for (var i = 0; i < 16 && i < store.Count; i++)
        {
            if (cursors[channel] >= store.Count) cursors[channel] = 0;
            var handle = HandleAt(store, cursors[channel]++);
            if (!store.TryGet(handle, out var state)) continue;
            var pos = new BlockPos((int)Math.Floor(state.Position.X), (int)Math.Floor(state.Position.Y), (int)Math.Floor(state.Position.Z), 0);
            var contact = pollution.Plants.At(pos, now);
            var key = (channel, handle);
            var dt = contacts.TryGetValue(key, out var previous) ? Math.Clamp((now - previous) / 1000d, 0, 5) : 0;
            if (contacts.Count < 8192 || contacts.ContainsKey(key)) contacts[key] = now;
            // Each gas has its own foliage retention. The soot authority remains native;
            // organic mist never enters it, so it cannot add soot/asphyxia damage.
            var factor = channel == 4 ? PollutionTransport.VegetationRetention(contact.Soot, dt, soot: true)
                : PollutionTransport.VegetationRetention(contact.Active, dt, channel);
            if (factor < 1) store.TrySet(handle, state with { Amount = state.Amount * (float)factor, Energy = state.Energy * (float)factor });
            if (channel is 2 or 3 && state.Amount > .01 && particleBudget > 0 && api.World.Rand.NextDouble() < .03)
            {
                particleBudget--;
                var p = new Vec3d(state.Position.X, state.Position.Y, state.Position.Z);
                SpawnMist(p, new((float)state.Velocity.X, (float)state.Velocity.Y, (float)state.Velocity.Z));
            }
        }
    }
    private void SpawnMist(Vec3d point, Vec3f velocity)
    {
        var packet = new OrganicMistParticle { X = point.X, Y = point.Y, Z = point.Z,
            Vx = velocity.X, Vy = velocity.Y, Vz = velocity.Z };
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            var pos = player.Entity?.Pos;
            if (pos is null || pos.Dimension != 0) continue;
            var dx = pos.X - point.X; var dy = pos.Y - point.Y; var dz = pos.Z - point.Z;
            if (dx * dx + dy * dy + dz * dz <= 32 * 32) visualChannel.SendPacket(packet, player);
        }
    }

    // Use Real Smoke's sphere renderer without adding our organic gas to its soot authority.
    internal static void RenderMist(ICoreClientAPI api, OrganicMistParticle packet)
    {
        if (!double.IsFinite(packet.X) || !double.IsFinite(packet.Y) || !double.IsFinite(packet.Z)
            || !float.IsFinite(packet.Vx) || !float.IsFinite(packet.Vy) || !float.IsFinite(packet.Vz)) return;
        var spheres = api.ModLoader.GetModSystem<SphereParticleSystem>(true);
        if (spheres is null || spheres.LivePoolLimit == 0) return;
        var rand = api.World.Rand;
        spheres.Spawn(new SphereParticleSpawn {
            PositionX = packet.X, PositionY = packet.Y, PositionZ = packet.Z,
            VelocityX = Math.Clamp(packet.Vx, -.3f, .3f) + (float)(rand.NextDouble() - .5) * .06f,
            VelocityY = Math.Clamp(packet.Vy, -.2f, .2f) + .015f,
            VelocityZ = Math.Clamp(packet.Vz, -.3f, .3f) + (float)(rand.NextDouble() - .5) * .06f,
            RollAxisY = 1, RollMultiplier = .4f, Radius = .2f + (float)rand.NextDouble() * .08f,
            GrowthRate = .035f, PersistentDepth = .3f, LifeLength = 6f,
            Bounciness = .2f, ColorRed = 101, ColorGreen = 107, ColorBlue = 62,
            HighlightRed = 143, HighlightGreen = 145, HighlightBlue = 90, HighlightShare = 65
        });
    }
    private static void AfterPump(BlockEntityBehaviorRealGasPump __instance)
    {
        var controller = AccessTools.Field(__instance.GetType(), "smokeController").GetValue(__instance);
        foreach (var bridge in Instances)
            if (bridge.Ready && ReferenceEquals(bridge.native, controller))
                try { bridge.Pump(__instance); } catch (Exception ex) { bridge.Fail(ex); }
    }
    private void Pump(BlockEntityBehaviorRealGasPump pump)
    {
        var type = typeof(BlockEntityBehaviorRealGasPump);
        if (AccessTools.Property(type, "CanPump").GetValue(pump) is not true) return;
        var input = (Vec3d)AccessTools.Field(type, "inputCenter").GetValue(pump)!;
        var end = (BlockPos)AccessTools.Field(type, "endPos").GetValue(pump)!;
        var output = pump.NextOutputLocation();
        if (end.dimension != 0 || output is null || output.dimension != 0 || api.World.BlockAccessor.GetChunkAtBlockPos(output) is null
            || !GasUtils.IsAdjacentSidePorous(api.World.BlockAccessor, end, output)) return;
        var limit = (int)AccessTools.Field(type, "amountLimit").GetValue(pump)! * Scale;
        for (var offset = 0; offset < 4 && limit > 0; offset++)
        {
            var channel = (offset + (int)(step % 4)) % 4;
            channels[channel].CopyStatesInRange(input, .5, .5, handles, states);
            for (var i = 0; i < handles.Count && limit > 0; i++)
            {
                var position = new FastVec3d(output.X + .5, output.Y + .1, output.Z + .5);
                var velocity = new FastVec3d((output.X - end.X) * .5, (output.Y - end.Y) * .5, (output.Z - end.Z) * .5);
                limit -= MoveParcel(channels[channel], handles[i], position, velocity, limit);
            }
        }
    }
    internal static float MoveParcel(SmokeParcelAuthority authority, SmokeParcelHandle handle,
        FastVec3d position, FastVec3d velocity, float limit)
    {
        if (!float.IsFinite(limit) || limit <= 0 || !authority.TryGet(handle, out var state)) return 0;
        var amount = Math.Min(state.Amount, limit);
        if (amount <= 0) return 0;
        if (amount == state.Amount) return authority.TryRelocate(handle, position, velocity) ? amount : 0;
        // Float-preserving split. Native integer pumps lose weak gas parcels, and
        // native capacity eviction must never delete the source of a split.
        if (authority.Count >= authority.CapacityLimit) return 0;
        var fraction = amount / state.Amount;
        if (!authority.TryCreate(state with { Amount = amount, Energy = state.Energy * fraction,
            Position = position, PreviousPosition = position, Velocity = velocity }, out var split)) return 0;
        if (Store(authority).TrySet(handle, state with { Amount = state.Amount - amount, Energy = state.Energy * (1 - fraction) })) return amount;
        authority.TryRemove(split);
        return 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Ready = false; Instances.Remove(this);
        if (listener != 0) api.Event.UnregisterGameTickListener(listener);
        harmony.UnpatchAll(harmony.Id); simulator.Dispose();
        foreach (var channel in channels) channel.Clear();
    }
}
