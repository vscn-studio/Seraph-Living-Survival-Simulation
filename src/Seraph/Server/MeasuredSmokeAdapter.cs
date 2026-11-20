using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.Server;

/// <summary>Optional Real Smoke relative amount adapter. Native breath/asphyxia and emissions remain external.</summary>
internal sealed class MeasuredSmokeAdapter : IDisposable
{
    private readonly ICoreServerAPI api;
    private System.Func<Vec3d, double>? query;
    private readonly Dictionary<string, long> owned = [];
    private readonly Harmony patch = new("vsslssmod.smoke-dose");
    private static readonly List<MeasuredSmokeAdapter> Instances = [];
    [ThreadStatic] private static int firepitScope;
    internal bool Ready => query is not null;
    internal string Status { get; private set; } = "unbound";
    internal long Samples { get; private set; }
    internal static object Diagnostics() => Instances.Select(a => new { a.Status, a.Ready, a.Samples }).ToArray();
    internal static bool AnyReady => Instances.Any(a => a.Ready);

    internal MeasuredSmokeAdapter(ICoreServerAPI api)
    {
        this.api = api;
        Instances.Add(this);
        try
        {
            var system = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "RealSmoke.ModSystem");
            if (system is null) { Status = "Real Smoke not loaded"; return; }
            var controller = system.GetType().GetProperty("SmokeController")?.GetValue(system);
            if (controller is null) { Status = "Real Smoke controller unavailable"; return; }
            query = BindQuery(controller);
            Status = "bound: Real Smoke raw amount / 8; eye cylinder radius/half-height 0.5; dimension 0";
            // Only this named firepit path may relinquish saturation drain. Fumes, tannin and scent retain ownership.
            var scent = AccessTools.TypeByName("SmokeScent.SmokeScentMod");
            var tick = scent is null ? null : AccessTools.Method(scent, "TickFirepitSmoke", [typeof(float)]);
            var drain = scent is null ? null : AccessTools.Method(scent, "DrainSaturation", [typeof(EntityPlayer), typeof(float)]);
            if (tick is not null && drain is not null)
            {
                patch.Patch(tick, prefix: new HarmonyMethod(typeof(MeasuredSmokeAdapter), nameof(BeginFirepit)),
                    finalizer: new HarmonyMethod(typeof(MeasuredSmokeAdapter), nameof(EndFirepit)));
                patch.Patch(drain, prefix: new HarmonyMethod(typeof(MeasuredSmokeAdapter), nameof(AllowDrain)));
            }
        }
        catch (Exception ex) { query = null; Status = "unavailable: " + ex.GetType().Name; api.Logger.Warning("[vsslssmod] Smoke dose adapter {0}", Status); }
    }

    internal static System.Func<Vec3d, double> BindQuery(object controller)
    {
        var region = controller.GetType().Assembly.GetType("RealSmoke.Smoke.Simulation.SmokeQueryCylinder", true)!;
        var constructor = region.GetConstructor([typeof(Vec3d), typeof(double), typeof(double)])
            ?? throw new MissingMethodException("Smoke cylinder constructor");
        var generic = controller.GetType().GetMethods().Single(m => m.Name == "GetSmokeAmount" && m.IsGenericMethodDefinition
            && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1).MakeGenericMethod(region);
        if (generic.ReturnType != typeof(int) || generic.GetParameters()[0].ParameterType != region.MakeByRefType())
            throw new MissingMethodException("Smoke amount signature");
        var center = Expression.Parameter(typeof(Vec3d)); var local = Expression.Variable(region);
        var call = Expression.Call(Expression.Constant(controller), generic, local);
        return Expression.Lambda<System.Func<Vec3d, double>>(Expression.Block([local],
            Expression.Assign(local, Expression.New(constructor, center, Expression.Constant(.5), Expression.Constant(.5))),
            Expression.Divide(Expression.Convert(call, typeof(double)), Expression.Constant(8d))), center).Compile();
    }

    internal bool TrySample(EntityPlayer player, out double concentration)
    {
        concentration = 0;
        if (query is null || player.Pos.Dimension != 0) { owned.Remove(player.PlayerUID); return false; }
        try
        {
            var eye = player.Pos.XYZ.Add(player.LocalEyePos);
            concentration = query(eye);
            if (!double.IsFinite(concentration) || concentration < 0) throw new InvalidOperationException("Invalid smoke amount");
            Samples++; return true;
        }
        catch (Exception ex)
        {
            query = null; owned.Clear(); Status = "query failed: " + ex.GetType().Name;
            api.Logger.Warning("[vsslssmod] Smoke dose adapter {0}; external penalties retained", Status); return false;
        }
    }
    internal void Ownership(EntityPlayer player, bool active)
    { if (active) owned[player.PlayerUID] = api.World.ElapsedMilliseconds; else owned.Remove(player.PlayerUID); }
    private static void BeginFirepit() => firepitScope++;
    private static Exception? EndFirepit(Exception? __exception) { firepitScope = Math.Max(0, firepitScope - 1); return __exception; }
    internal static bool AllowDrain(EntityPlayer __0) => firepitScope == 0 || !Instances.Any(a => a.Ready
        && ReferenceEquals(a.api.World, __0.World) && __0.Alive && __0.Pos.Dimension == 0
        && a.owned.TryGetValue(__0.PlayerUID, out var at) && a.api.World.ElapsedMilliseconds - at is >= 0 and <= 500);
    public void Dispose() { Instances.Remove(this); owned.Clear(); query = null; patch.UnpatchAll(patch.Id); }
}
