using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.MathTools;

internal static class SmokeNativeChecks
{
    // Real optional DLL contract and parcel query; no game world or external ModSystem is started.
    internal static void Run(Assembly mod, string smokePath, string scentPath)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(smokePath));
        // The shipped flue adapter must still bind to the installed optional behavior.
        using var patchConfig = System.Text.Json.JsonDocument.Parse(File.ReadAllText("assets/vsslssmod/patches/realsmoke-chimney.json"));
        var patchEntry = patchConfig.RootElement[0];
        var pump = patchEntry.GetProperty("value")[0];
        var pumpType = assembly.GetTypes().Single(t => t.Name == pump.GetProperty("name").GetString());
        var pumpSettings = pump.GetProperty("properties");
        if (!typeof(Vintagestory.API.Common.BlockEntityBehavior).IsAssignableFrom(pumpType)
            || patchEntry.GetProperty("file").GetString() != "vsslssmod:blocktypes/chimney.json"
            || pumpSettings.GetProperty("inputLocation").GetString() != "below"
            || pumpSettings.GetProperty("outputLocations")[0].GetString() != "above"
            || pumpSettings.GetProperty("outputDistanceFromInput").GetInt32() != 1
            || pumpSettings.GetProperty("amountLimit").GetInt32() != 32)
            throw new Exception("Embedded chimney no longer matches the installed Real Smoke pump contract");
        var controllerType = assembly.GetType("RealSmoke.Smoke.Simulation.SmokeController", true)!;
        var controller = RuntimeHelpers.GetUninitializedObject(controllerType);
        var authorityField = controllerType.GetField("parcelAuthority", all)!;
        authorityField.SetValue(controller, Activator.CreateInstance(authorityField.FieldType, [16]));
        var stateType = assembly.GetType("RealSmoke.Smoke.Simulation.SmokeParcelState", true)!;
        var state = Activator.CreateInstance(stateType)!;
        stateType.GetProperty("Position")!.SetValue(state, new FastVec3d(12, 61.6, 34));
        stateType.GetProperty("Amount")!.SetValue(state, 16f);
        var emit = controllerType.GetMethod("TryEmitParcel")!;
        if (!(bool)emit.Invoke(controller, [state, null])!) throw new Exception("Actual smoke parcel insertion failed");
        var adapterType = mod.GetType("Seraph.Server.MeasuredSmokeAdapter", true)!;
        var query = (System.Func<Vec3d, double>)adapterType.GetMethod("BindQuery", all)!.Invoke(null, [controller])!;
        if (Math.Abs(query(new(12, 61.6, 34)) - 2) > 1e-8 || query(new(100, 100, 100)) != 0)
            throw new Exception("Actual smoke cylinder query or amount conversion failed");
        var scent = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(scentPath)).GetType("SmokeScent.SmokeScentMod", true)!;
        var patch = new Harmony("vsslssmod.smoke-contract-test");
        try
        {
            GasContracts(mod, assembly, patch);
            patch.Patch(scent.GetMethod("TickFirepitSmoke", all, [typeof(float)])!,
                prefix: new HarmonyMethod(adapterType, "BeginFirepit"), finalizer: new HarmonyMethod(adapterType, "EndFirepit"));
            patch.Patch(scent.GetMethod("DrainSaturation", all, [typeof(Vintagestory.API.Common.EntityPlayer), typeof(float)])!,
                prefix: new HarmonyMethod(adapterType, "AllowDrain"));
        }
        finally { patch.UnpatchAll(patch.Id); }
        Console.WriteLine("PASS actual RealSmoke gas channels, fractional pump mass/energy, capacity, vent/pump Harmony and SmokeScent contracts (not gameplay acceptance)");
    }

    private static void GasContracts(Assembly mod, Assembly smoke, Harmony patch)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var bridgeType = mod.GetType("Seraph.Server.RealSmokeGasTransport", true)!;
        var utils = smoke.GetType("RealSmoke.SmokeUtils", true)!;
        foreach (var (target, after) in new[] { ("TryVent", "AfterVent"), ("TryVentAtPoint", "AfterPoint") })
            patch.Patch(utils.GetMethod(target, all)!, prefix: new HarmonyMethod(bridgeType, "BeforeVent"), postfix: new HarmonyMethod(bridgeType, after));
        patch.Patch(smoke.GetType("RealSmoke.BlockEntityBehaviorRealGasPump", true)!.GetMethod("PumpGas", all)!,
            postfix: new HarmonyMethod(bridgeType, "AfterPump"));

        var authorityType = smoke.GetType("RealSmoke.Smoke.Simulation.SmokeParcelAuthority", true)!;
        var stateType = smoke.GetType("RealSmoke.Smoke.Simulation.SmokeParcelState", true)!;
        var handleType = smoke.GetType("RealSmoke.Smoke.Simulation.SmokeParcelHandle", true)!;
        var bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
        var channels = Array.CreateInstance(authorityType, 4);
        for (var c = 0; c < 4; c++) channels.SetValue(Activator.CreateInstance(authorityType, [4]), c);
        bridgeType.GetField("channels", all)!.SetValue(bridge, channels);
        bridgeType.GetField("handles", all)!.SetValue(bridge, Activator.CreateInstance(typeof(List<>).MakeGenericType(handleType)));
        bridgeType.GetField("states", all)!.SetValue(bridge, Activator.CreateInstance(typeof(List<>).MakeGenericType(stateType)));
        bridgeType.GetProperty("Ready")!.SetValue(bridge, true);
        var emit = bridgeType.GetMethod("Emit", all)!;
        var sample = bridgeType.GetMethod("Sample")!;
        var p = new FastVec3d(12, 61.6, 34);
        emit.Invoke(bridge, [2, p, new FastVec3d(), 20f, .001d]);
        var chemistry = (Seraph.Core.Framework.AirChemistry)sample.Invoke(bridge, [new Vec3d(12, 61.6, 34), 0])!;
        Check(chemistry.NH3 > 0 && chemistry.CO == 0 && chemistry.SO2 == 0 && chemistry.H2S == 0,
            "Weak organic gas was truncated or mixed into another species.");
        Check(((Seraph.Core.Framework.AirChemistry)sample.Invoke(bridge, [new Vec3d(12, 61.6, 34), 1])!).NH3 == 0,
            "Gas sample crossed dimensions.");
        var authority = channels.GetValue(2)!;
        var handles = Activator.CreateInstance(typeof(List<>).MakeGenericType(handleType))!;
        var states = Activator.CreateInstance(typeof(List<>).MakeGenericType(stateType))!;
        object[] Query(Vec3d center)
        {
            authorityType.GetMethod("CopyStatesInRange")!.Invoke(authority, [center, .5d, .5d, handles, states]);
            return ((System.Collections.IEnumerable)states).Cast<object>().ToArray();
        }
        double Sum(object[] values, string property) => values.Sum(s => Convert.ToDouble(stateType.GetProperty(property)!.GetValue(s)));
        var inlet = new Vec3d(12, 61.6, 34);
        var initial = Query(inlet);
        var energy = Sum(initial, "Energy");
        var handle = ((System.Collections.IList)handles)[0]!;
        var moved = (float)bridgeType.GetMethod("MoveParcel", all)!.Invoke(null,
            [authority, handle, new FastVec3d(20, 63, 34), new FastVec3d(), .04f])!;
        Check(Math.Abs(moved - .04f) < 1e-7, "Fractional pump failed.");
        var remaining = Query(inlet);
        var output = Query(new(20, 63, 34));
        Check(Math.Abs(Sum(remaining, "Amount") + Sum(output, "Amount") - .1) < 1e-6
            && Math.Abs(Sum(remaining, "Energy") + Sum(output, "Energy") - energy) < 1e-4,
            "Pump split did not conserve gas mass and energy.");
        for (var i = 0; i < 8; i++) emit.Invoke(bridge, [2, p, new FastVec3d(), 20f, .001d]);
        Check((int)authorityType.GetProperty("Count")!.GetValue(authority)! == 4, "Gas capacity exceeded.");
        var full = Query(inlet);
        handle = ((System.Collections.IList)handles)[0]!;
        Check((float)bridgeType.GetMethod("MoveParcel", all)!.Invoke(null,
            [authority, handle, new FastVec3d(20, 63, 34), new FastVec3d(), .01f])! == 0
            && Math.Abs(Sum(Query(inlet), "Amount") - Sum(full, "Amount")) < 1e-7,
            "Full-capacity pump evicted or modified its source.");
    }
}
