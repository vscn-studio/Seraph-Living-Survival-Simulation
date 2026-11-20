using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.Client;

/// <summary>Native translucent cube highlights; no custom GL resources or world scans.</summary>
internal sealed class PollutionDebugHighlights : IDisposable
{
    private const int FirstSlot = 46160;
    private readonly ICoreClientAPI api;
    private readonly Func<ServerSnapshot?> snapshot;
    private readonly long listener;
    private PollutionDebugVolume[]? shown;
    private (int X, int Y, int Z, int Dimension) shownEye;
    private bool active, disposed;

    internal PollutionDebugHighlights(ICoreClientAPI api, Func<ServerSnapshot?> snapshot)
    {
        this.api = api; this.snapshot = snapshot;
        listener = api.Event.RegisterGameTickListener(_ => Update(), 250);
    }

    internal void Update()
    {
        if (disposed) return;
        var player = api.World.Player;
        var state = snapshot();
        if (player is null || state is not { CanViewDiagnostics: true }) { Clear(); return; }
        var eye = player.Entity.Pos.AsBlockPos;
        var volumes = state.PollutionDebugVolumes;
        var eyeKey = (eye.X, eye.Y, eye.Z, eye.dimension);
        if (ReferenceEquals(shown, volumes) && shownEye == eyeKey) return;
        shownEye = eyeKey; shown = volumes;
        // Native absolute highlights use full cubes. Give sources/barriers priority
        // over paths so coincident translucent faces do not obscure their colors.
        var visible = volumes.Take(768).Where(p => p.Dimension == eye.dimension
            && Math.Abs((long)p.X - eye.X) <= 16 && Math.Abs((long)p.Y - eye.Y) <= 16
            && Math.Abs((long)p.Z - eye.Z) <= 16)
            .OrderBy(p => p.Kind == PollutionDebugKind.AirPath ? 3 : p.Kind == PollutionDebugKind.WaterPath ? 2
                : p.Kind == PollutionDebugKind.Barrier ? 1 : 0).DistinctBy(p => (p.X, p.Y, p.Z)).ToArray();
        for (var k = 0; k <= (int)PollutionDebugKind.Barrier; k++)
        {
            var kind = (PollutionDebugKind)k;
            var blocks = visible.Where(p => p.Kind == kind).Select(p => new BlockPos(p.X, p.Y, p.Z, p.Dimension)).ToList();
            var color = kind switch {
                PollutionDebugKind.Source => ColorUtil.ToRgba(100, 255, 130, 30),
                PollutionDebugKind.RestrictedSource => ColorUtil.ToRgba(100, 245, 220, 50),
                PollutionDebugKind.SealedSource => ColorUtil.ToRgba(100, 170, 70, 230),
                PollutionDebugKind.WaterPath => ColorUtil.ToRgba(70, 40, 100, 255),
                PollutionDebugKind.AirPath => ColorUtil.ToRgba(60, 40, 230, 225),
                _ => ColorUtil.ToRgba(85, 255, 45, 45) };
            api.World.HighlightBlocks(player, FirstSlot + k, blocks, blocks.Select(_ => color).ToList(),
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary, 1);
        }
        active = true;
    }

    internal void Clear()
    {
        shown = null;
        if (!active) return;
        active = false;
        if (api.World.Player is not { } player) return;
        for (var k = 0; k <= (int)PollutionDebugKind.Barrier; k++)
            api.World.HighlightBlocks(player, FirstSlot + k, [], [], EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary, 1);
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear(); disposed = true;
        api.Event.UnregisterGameTickListener(listener);
    }
}
