using System.Diagnostics;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.Diagnostics;

internal sealed class PerformanceRecorder : IDisposable
{
    private readonly ICoreAPI api;
    private readonly string side;
    private readonly PerformanceContext context;
    private Task save = Task.CompletedTask;
    private bool kometWasCapturing, initialized;
    private string trigger = "";
    private int seconds;
    private long lease;
    internal PerformanceCapture? Current => context.Current;
    internal string? LastPath { get; private set; }
    internal PerformanceRecorder(ICoreAPI api, string side)
    { this.api = api; this.side = side; context = new(api); }
    internal bool Start(int duration, string source)
    {
        if (Current is not null || !save.IsCompleted || duration is < 1 or > 600) return false;
        seconds = duration; trigger = source; context.Start(side, source); return true;
    }
    internal void RenewLease()
    {
        if (Current is null) Start(600, "remote");
        lease = Stopwatch.GetTimestamp();
    }
    internal void Tick()
    {
        if (!initialized) { initialized = true; KometTimingBridge.Initialize(api); }
        var komet = KometTimingBridge.Capturing;
        if (komet && !kometWasCapturing && Current is null) Start(600, "komet");
        if (Current is { } capture && (capture.ElapsedSeconds >= seconds
            || trigger == "komet" && !komet
            || trigger == "remote" && Stopwatch.GetElapsedTime(lease).TotalSeconds >= 10))
            Stop("completed");
        kometWasCapturing = komet;
    }
    internal string? Stop(string reason)
    {
        var report = context.Stop(reason);
        if (report is null) return LastPath;
        var directory = Path.Combine(GamePaths.Logs, "vsslssmod-perf");
        LastPath = Path.Combine(directory, $"{report.StartedUtc:yyyyMMdd-HHmmss}-{side}-{report.Id}.json");
        var path = LastPath;
        // Snapshot is frozen before dispatch. No per-frame IO, game state or player data in the worker.
        save = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var old in new DirectoryInfo(directory).GetFiles($"*-{side}-*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(20)) old.Delete();
                api.Logger.Notification("[vsslssmod] Performance capture saved: {0}", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { api.Logger.Error("[vsslssmod] Performance capture write failed: {0}", ex.Message); }
        });
        return path;
    }
    public void Dispose() { Stop("leave-world"); context.Dispose(); }
}
