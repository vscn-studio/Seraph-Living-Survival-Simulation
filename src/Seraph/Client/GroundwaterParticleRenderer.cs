using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.Client;

/// <summary>
/// Renders a short-lived, client-only hint for a server-authorized virtual
/// aquifer. It never places a block or draws a custom water surface.
/// </summary>
internal sealed class GroundwaterParticleRenderer : IDisposable
{
    private const int MaxReveals = 12;
    private const long SpawnIntervalMs = 1000;
    private const int MaxParticlesPerReveal = 20;
    private const int MaxDistance = 32;
    // White is intentional: ColorByBlock applies the vanilla water tint and
    // prevents the seepage from inheriting a debug or custom hue.
    private static readonly int WaterParticleColor = ColorUtil.ToRgba(255, 255, 255, 220);
    private readonly ICoreClientAPI api;
    private readonly Block? vanillaWaterBlock;
    private readonly Dictionary<RevealKey, GroundwaterRevealState> reveals = new();
    private readonly long listener;
    private long lastSpawnMs;

    private readonly record struct RevealKey(int X, int Y, int Z, int Dimension);
    private sealed class GroundwaterRevealState
    {
        public required GroundwaterReveal Packet { get; init; }
        public long ExpiresAt { get; init; }
    }

    internal GroundwaterParticleRenderer(ICoreClientAPI api)
    {
        this.api = api;
        vanillaWaterBlock = api.World.GetBlock(new AssetLocation("game:water-still-7"));
        listener = api.Event.RegisterGameTickListener(_ => Tick(), 250);
    }

    internal void Reveal(GroundwaterReveal packet)
    {
        if (packet.Dimension != api.World.Player?.Entity.Pos.Dimension
            || !packet.ExcavationExposure
            || !float.IsFinite(packet.SurfaceY)
            || packet.Radius is < 1 or > 32) return;

        var key = new RevealKey(packet.X, packet.Y, packet.Z, packet.Dimension);
        var duration = Math.Clamp(packet.DurationMs, 1000, 900000);
        reveals[key] = new GroundwaterRevealState { Packet = packet,
            ExpiresAt = api.World.ElapsedMilliseconds + duration };
        while (reveals.Count > MaxReveals)
        {
            var oldest = reveals.OrderBy(pair => pair.Value.ExpiresAt).First();
            reveals.Remove(oldest.Key);
        }
    }

    internal void Clear() => reveals.Clear();

    private void Tick()
    {
        if (reveals.Count == 0 || api.World.Player?.Entity is not { } player) return;
        var now = api.World.ElapsedMilliseconds;
        foreach (var key in reveals.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            reveals.Remove(key);
        if (reveals.Count == 0 || now - lastSpawnMs < SpawnIntervalMs) return;
        lastSpawnMs = now;

        var playerPos = player.Pos.XYZ;
        foreach (var reveal in reveals.Values)
        {
            var packet = reveal.Packet;
            var center = new Vec3d(packet.X + 0.5, packet.Y, packet.Z + 0.5);
            if (center.DistanceTo(playerPos) > Math.Min(MaxDistance, packet.Radius + 20)) continue;
            SpawnExcavationParticles(packet, center);
        }
    }

    private void SpawnExcavationParticles(GroundwaterReveal packet, Vec3d center)
    {
        var emitted = 0;
        for (var faceIndex = 0; faceIndex < Faces.Length; faceIndex++)
        {
            if (emitted >= MaxParticlesPerReveal) break;
            if ((packet.SaturatedFaceMask & Faces[faceIndex].Bit) == 0) continue;
            var face = Faces[faceIndex];
            // Keep the emitter in a thin slab on the exposed vertical face.
            // Rainy Glass follows the face normal and gravity; this fallback
            // does the same without a billboard quad.
            var min = new Vec3d(center.X + face.MinX, center.Y + 0.06, center.Z + face.MinZ);
            var max = new Vec3d(center.X + face.MaxX, center.Y + 0.94, center.Z + face.MaxZ);
            var minVelocity = new Vec3f(face.Dx * 0.012f - 0.006f, -0.18f, face.Dz * 0.012f - 0.006f);
            var maxVelocity = new Vec3f(face.Dx * 0.012f + 0.006f, -0.04f, face.Dz * 0.012f + 0.006f);
            var quantity = Math.Clamp((int)Math.Ceiling(packet.WaterContent * 5), 1, 5);
            quantity = Math.Min(quantity, MaxParticlesPerReveal - emitted);
            // Cube particles have a stable volume and do not billboard into the
            // camera-facing X shape produced by vanilla quads.  The spawn box
            // remains inset against the exposed face, while gravity carries the
            // drops down that face. Match BlockBehaviorRainDrip's native drip
            // size; sub-tenth sizes are nearly invisible at normal view range.
            var particles = new SimpleParticleProperties(quantity, quantity, WaterParticleColor,
                min, max, minVelocity, maxVelocity, 1.25f, 0.35f, 0.6f, 0.8f, EnumParticleModel.Cube)
            {
                ColorByBlock = vanillaWaterBlock,
                WithTerrainCollision = true,
                ShouldDieInLiquid = true
            };
            api.World.SpawnParticles(particles);
            emitted += quantity;
        }
    }

    private static readonly Face[] Faces =
    [
        new(0, 0, -1, -0.38, 0.38, -0.49, -0.44, 1),
        new(0, 0, 1, -0.38, 0.38, 0.44, 0.49, 2),
        new(-1, 0, 0, -0.49, -0.44, -0.38, 0.38, 4),
        new(1, 0, 0, 0.44, 0.49, -0.38, 0.38, 8)
    ];

    private readonly record struct Face(int Dx, int Dy, int Dz, double MinX, double MaxX, double MinZ, double MaxZ, byte Bit);

    public void Dispose()
    {
        api.Event.UnregisterGameTickListener(listener);
        reveals.Clear();
    }
}
