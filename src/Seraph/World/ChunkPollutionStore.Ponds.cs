using System.Diagnostics;
using System.Text.Json;
using Seraph.Core.Framework;
using Vintagestory.API.MathTools;

namespace Seraph.World;

internal sealed partial class ChunkPollutionStore
{
    private const string PondKey = "vsslssmod:pond-cells-v1";
    private sealed record PondCell(int X, int Y, int Z, double Hour, PondContamination Load);
    private IEnumerator<bool>? pondWork;
    private double PondHour => api.World.Calendar is { } calendar && calendar.HoursPerDay > 0
        ? calendar.TotalHours / calendar.HoursPerDay * 24 : 0;

    private PondContamination PondAt(BlockPos pos, double? at = null)
    {
        if (!columns.TryGetValue(Key(pos), out var column)
            || !column.PondCells.TryGetValue((pos.X, pos.Y, pos.Z), out var saved)) return new();
        return saved.Load.Scale(Math.Pow(.5, Math.Max(0, (at ?? PondHour) - saved.Hour) / PondContaminationModel.HalfLifeHours));
    }

    private void AdvancePonds()
    {
        var watch = Stopwatch.StartNew();
        // CPU work as well as world reads is bounded. Tooltip calls never drive transport.
        pondWork ??= MixPonds().GetEnumerator();
        for (var operations = 0; operations < 128 && watch.Elapsed.TotalMilliseconds < 1; operations++)
            if (!pondWork.MoveNext()) { pondWork.Dispose(); pondWork = null; break; }
    }

    private IEnumerable<bool> MixPonds()
    {
        var classifier = WaterQuality.Classifier(api.World);
        if (classifier is null) yield break;
        // Cleanup also bounds water left behind by digging, draining or moving a pond.
        foreach (var pair in columns.ToArray())
            foreach (var p in pair.Value.PondCells.Values.ToArray())
            {
                var pos = new BlockPos(p.X, p.Y, p.Z, pair.Key.Dimension);
                var block = ReadRouteBlock(pos);
                if (block is not null && string.IsNullOrEmpty(block.LiquidCode) && !block.IsLiquid())
                    pair.Value.PondCells.Remove((p.X, p.Y, p.Z));
                yield return true;
            }
        foreach (var step in TransferToPonds(classifier)) yield return step;
        foreach (var body in classifier.Ponds.Take(64).ToArray())
        {
            var cells = body.Cells;
            var owners = new Dictionary<PollutionChunk, Column>();
            var start = double.NegativeInfinity;
            var valid = true;
            for (var i = 0; i < cells.Count; i++)
            {
                var p = cells[i];
                if (!columns.TryGetValue(Key(p), out var c) || c.Region is null || c.FormatRejected
                    || !c.Chunks.TryGetValue(p.Y >> 5, out var chunk) || chunk.Disposed) { valid = false; break; }
                owners[Key(p)] = c;
                if (c.PondCells.TryGetValue((p.X, p.Y, p.Z), out var saved)) start = Math.Max(start, saved.Hour);
                yield return true;
            }
            if (!valid) continue;
            var now = PondHour;
            if (!double.IsFinite(now)) continue;
            if (!double.IsFinite(start) || start > now) start = now;
            var hours = Math.Min(.25, now - start);
            // New bodies first get a zero ledger so a future tick has a stable clock.
            var old = new PondContamination[cells.Count];
            for (var i = 0; i < cells.Count; i++) { old[i] = PondAt(cells[i], start); yield return true; }
            if (hours is > 0 and < RegionalEnvironmentModel.RegionalIntervalHours) continue;
            var mean = PondContaminationModel.Mean(old);
            var zero = new PondContamination();
            var next = new PondCell[cells.Count];
            for (var i = 0; i < cells.Count; i++)
            {
                var p = cells[i];
                next[i] = new(p.X, p.Y, p.Z, start + hours,
                    PondContaminationModel.AdvanceCell(old[i], mean, zero, zero, hours));
                yield return true;
            }
            // Publication is atomic on the main thread (at most 2000 assignments, no world reads).
            // An invalidated/split/unloaded body never publishes an obsolete mixing result.
            if (classifier.PondAt(cells[0]) != body || owners.Any(p => !columns.TryGetValue(p.Key, out var live)
                || !ReferenceEquals(live, p.Value) || live.FormatRejected
                || live.PondCells.Count + cells.Count(cell => Key(cell) == p.Key
                    && !live.PondCells.ContainsKey((cell.X, cell.Y, cell.Z))) > 32768)) continue;
            foreach (var saved in next)
                owners[Key(new(saved.X, saved.Y, saved.Z, cells[0].dimension))].PondCells[(saved.X, saved.Y, saved.Z)] = saved;
        }
    }

    private IEnumerable<bool> TransferToPonds(SurfaceWaterClassifier classifier)
    {
        var ready = new HashSet<SurfaceWaterClassifier.Pond>();
        foreach (var body in classifier.Ponds.Take(64).ToArray())
        {
            var complete = true;
            foreach (var p in body.Cells)
            {
                if (!columns.TryGetValue(Key(p), out var c) || c.Region is null || c.FormatRejected
                    || !c.Chunks.TryGetValue(p.Y >> 5, out var chunk) || chunk.Disposed) complete = false;
                yield return true;
            }
            if (complete && classifier.PondAt(body.Cells[0]) == body) ready.Add(body);
        }
        if (ready.Count == 0) yield break;
        // Scan each source route once per round, regardless of the number of receiving ponds.
        foreach (var pair in columns.ToArray())
            foreach (var location in pair.Value.Residues.Keys.ToArray())
            {
                var origin = new BlockPos(location.X, location.Y, location.Z, pair.Key.Dimension);
                var destinations = new Dictionary<SurfaceWaterClassifier.Pond, (BlockPos Entry, double Weight)>();
                foreach (var route in routes.WaterCells(origin))
                {
                    if (classifier.PondAt(route.Position) is { } body && ready.Contains(body)
                        && route.Weight > destinations.GetValueOrDefault(body).Weight)
                        destinations[body] = (route.Position, route.Weight);
                    yield return true;
                }
                if (destinations.Count == 0 || !columns.TryGetValue(pair.Key, out var owner) || !ReferenceEquals(owner, pair.Value)
                    || owner.FormatRejected || !owner.Residues.TryGetValue(location, out var site)) { yield return true; continue; }
                // Revalidate after yielded work. A wall edit or unload must not commit an obsolete path.
                if (destinations.Any(p => classifier.PondAt(p.Value.Entry) != p.Key || routes.Weight(origin, p.Value.Entry) <= 0
                    || !columns.TryGetValue(Key(p.Value.Entry), out var c) || c.FormatRejected
                    || c.PondCells.Count >= 32768 && !c.PondCells.ContainsKey((p.Value.Entry.X, p.Value.Entry.Y, p.Value.Entry.Z)))) continue;
                var now = PondHour;
                var hours = Math.Clamp(now - (site.PondTransferHour ?? now), 0, .25);
                var share = destinations.Values.Max(p => p.Weight);
                var fraction = (1 - Math.Exp(-hours)) * share;
                var totalWeight = destinations.Values.Sum(p => p.Weight);
                var moved = new PondContamination { Generic = site.State.SurfacePathogens,
                    Agents = site.State.SurfaceAgents }.Scale(fraction);
                // Consume the source stock that is deposited. Existing local surface
                // reservoirs are never billed again as recurring input rates. This also
                // migrates old saves gradually without copying their contamination.
                owner.Residues[location] = site with { PondTransferHour = now,
                    State = site.State with { SurfacePathogens = site.State.SurfacePathogens * (1 - fraction),
                        SurfaceAgents = PathogenLoads.Scale(site.State.SurfaceAgents, 1 - fraction) } };
                foreach (var destination in destinations.Values)
                {
                    var p = destination.Entry;
                    var c = columns[Key(p)];
                    var key = (p.X, p.Y, p.Z);
                    var old = c.PondCells.GetValueOrDefault(key) ?? new(p.X, p.Y, p.Z, now, new());
                    // Source reservoirs use a 16-water-block reference volume. Keep
                    // the mixing clock: deposits and mixing are serialized in this job.
                    var retention = PollutionTransport.RiparianRetention(Plants.At(p, api.World.ElapsedMilliseconds, water: true).Active);
                    c.PondCells[key] = old with { Load = old.Load.Add(moved.Scale(16 * destination.Weight / totalWeight * retention)) };
                }
                yield return true;
            }
    }

    private void RestorePonds(Column column)
    {
        var bytes = column.Chunks.OrderBy(p => p.Key).Select(p => p.Value.GetModdata(PondKey)).FirstOrDefault(b => b is not null);
        if (bytes is null) return;
        if (bytes.Length is 0 or > 16000000) throw new InvalidDataException("Invalid pond cell data length.");
        var saved = JsonSerializer.Deserialize<PondCell[]>(bytes, new JsonSerializerOptions {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
        if (saved is null || saved.Length > 32768 || saved.Any(p => p is null || !double.IsFinite(p.Hour)
            || p.Hour < 0 || p.Load is null || !p.Load.Valid())
            || saved.Select(p => (p.X, p.Y, p.Z)).Distinct().Count() != saved.Length)
            throw new InvalidDataException("Invalid pond cell state.");
        var now = PondHour;
        foreach (var p in saved)
            column.PondCells[(p.X, p.Y, p.Z)] = p with { Hour = now,
                Load = p.Load.Scale(Math.Pow(.5, Math.Max(0, now - p.Hour) / PondContaminationModel.HalfLifeHours)) };
    }
}
