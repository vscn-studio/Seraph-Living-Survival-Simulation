using System.Reflection;
using Seraph.Client;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class PollutionDebugChecks
{
    internal static void Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var entity = new EntityPlayer();
        entity.Pos.SetPos(0, 8, 0); entity.Pos.Dimension = 2;
        var player = FixtureClientPlayer.Make(entity);
        var slots = new Dictionary<int, (List<BlockPos> Blocks, int Color)>();
        var writes = 0; var unregistered = 0;
        var world = Proxy<IClientWorldAccessor>((m, a) => {
            if (m.Name == "get_Player") return player;
            if (m.Name != "HighlightBlocks") throw new Exception("Debug renderer scanned the world: " + m.Name);
            Check(ReferenceEquals(a![0], player) && (EnumHighlightShape)a[5]! == EnumHighlightShape.Arbitrary,
                "Highlights did not use native per-block cubes.");
            var blocks = (List<BlockPos>)a[2]!; var colors = (List<int>)a[3]!;
            Check(blocks.Count == colors.Count, "Highlight colors were lost.");
            slots[(int)a[1]!] = (blocks, colors.FirstOrDefault()); writes++; return null;
        });
        Action<float>? tick = null;
        var events = Proxy<IClientEventAPI>((m, a) => m.Name switch {
            "RegisterGameTickListener" => Register((Action<float>)a![0]!),
            "UnregisterGameTickListener" => Unregister(), _ => throw new Exception(m.Name) });
        object Register(Action<float> callback) { tick = callback; return 1L; }
        object? Unregister() { unregistered++; return null; }
        var api = Proxy<ICoreClientAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Event" => events, _ => throw new Exception(m.Name) });
        var volumes = Enum.GetValues<PollutionDebugKind>().Select((kind, i) => new PollutionDebugVolume {
            X = i, Y = 8, Dimension = 2, Kind = kind, Strength = .5f }).ToArray();
        var wire = ProtoBuf.Serializer.DeepClone(new ServerSnapshot { CanViewDiagnostics = true, PollutionDebugVolumes = volumes });
        Check(wire.PollutionDebugVolumes.Length == 6 && wire.PollutionDebugVolumes[5].Kind == PollutionDebugKind.Barrier
            && wire.PollutionDebugVolumes[0].Dimension == 2 && wire.PollutionDebugVolumes[0].Strength == .5f,
            "Debug geometry did not survive the real snapshot serializer.");
        ServerSnapshot? state = wire;
        using var highlights = new PollutionDebugHighlights(api, () => state);
        tick!(.25f);
        Check(slots.Count == 6 && slots.Values.Sum(s => s.Blocks.Count) == 6 && slots.Values.Select(s => s.Color).Distinct().Count() == 6,
            "Different debug categories lost their native highlights or distinct colors.");
        var before = writes; tick(.25f);
        Check(writes == before, "An unchanged debug snapshot rebuilt native meshes every tick.");
        state!.CanViewDiagnostics = false; tick(.25f);
        Check(slots.Values.All(s => s.Blocks.Count == 0), "Revoked diagnostics left geometry visible.");
        state.CanViewDiagnostics = true; tick(.25f);
        entity.Pos.Dimension = 1; tick(.25f);
        Check(slots.Values.All(s => s.Blocks.Count == 0), "Geometry crossed dimensions.");
        entity.Pos.Dimension = 2; tick(.25f);
        state = null; tick(.25f);
        Check(slots.Values.All(s => s.Blocks.Count == 0), "Stale/disconnected snapshots retained highlights.");
        state = wire; tick(.25f); highlights.Dispose(); highlights.Dispose();
        Check(unregistered == 1 && slots.Values.All(s => s.Blocks.Count == 0), "Debug disposal leaked a listener or native highlights.");
        Console.WriteLine("PASS pollution debug: snapshot serialization, native cube colors, cache reuse, permissions, stale data, dimensions and cleanup");
    }
}
