using System.Collections;
using System.Reflection;
using System.Text.Json;
using Seraph.Application;

internal static class RuntimeDiagnosticsChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var archiveType = mod.GetType("Seraph.Server.CompatibilityArchive", true)!;
        var inventory = (Array)JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/config/compatibility.json")), archiveType.MakeArrayType())!;
        if (inventory.Length < 5) throw new Exception("Compatibility catalog could not load scalar/array integration domains.");
        var method = mod.GetType("Seraph.Server.RuntimeDiagnostics", true)!.GetMethod("RuntimeFeatures", flags)!;
        Dictionary<string, (bool Ready, int Count)> Features(SeraphServerConfig config) => ((IEnumerable)method.Invoke(null,
            [CapabilityRegistry.Create(config, []), config])!).Cast<object>().ToDictionary(
                f => (string)f.GetType().GetProperty("Id")!.GetValue(f)!,
                f => ((bool)f.GetType().GetProperty("Ready")!.GetValue(f)!, (int)f.GetType().GetProperty("InstalledHookCount")!.GetValue(f)!));
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        var status = mod.GetType("Seraph.Server.RuntimeDiagnostics", true)!.GetMethod("CompatibilityStatus", flags)!;
        string Status(string id, bool exact, bool missing) => (string)status.Invoke(null, [id, exact, missing])!;
        Require(Status("slowtox", true, false) == "untested" && Status("slowtox", false, false) == "observe-only"
            && Status("content", true, true) == "dependency-missing" && Status("playerstatusstrip", true, false) == "unsupported"
            && Status("slowtoxvisualized", true, false) == "dependency-missing", "Compatibility status overstated acceptance or ignored exclusions.");
        var conflictMethod = mod.GetType("Seraph.Server.RuntimeDiagnostics", true)!.GetMethod("CompatibilityConflicts", flags)!;
        var conflictFeatureType = mod.GetType("Seraph.Server.RuntimeFeature", true)!;
        var emptyFeatures = Array.CreateInstance(conflictFeatureType, 0);
        var conflicts = (string[])conflictMethod.Invoke(null, [new[] { "realsmoke", "vscnservermodfix" },
            CapabilityRegistry.Create(new(), []), emptyFeatures])!;
        Require(conflicts.Any(x => x.Contains("realsmoke", StringComparison.Ordinal))
            && conflicts.Any(x => x.Contains("vscnservermodfix", StringComparison.Ordinal)), "High-risk reference integrations were not diagnosed.");
        var misleading = CapabilityRegistry.Create(new(), [("slowtoxvisualized", "SlowTox"), ("undeadcontent", "Dead"), ("comfybeds", "Comfy Beds")]);
        Require(misleading[Capability.Alcohol].Owner == CapabilityOwner.Native
            && misleading[Capability.DeathInventory].Owner == CapabilityOwner.Native
            && misleading[Capability.Sleep].Provider == "comfybeds", "Provider matching used display-name substrings.");
        Require(!Features(new())["finite-native-care"].Ready, "Missing care bridge reported ready.");
        using (var physiology = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativePhysiologyBridge", true)!, true)!)
        using (var bandages = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeBandageBridge", true)!, true)!)
        using (var oxygen = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeInjuryActionBridge", true)!, true)!)
        {
            var live = Features(new());
            Require(live["native-physiology"].Ready && live["finite-native-care"].Ready
                && live["finite-native-care"].Count == 2 && live["oxygen-recovery"].Ready, "Installed hooks were hidden by stale diagnostics.");
            Require(!Features(new() { Mode = "observe" })["native-physiology"].Ready
                && !Features(new() { EnableMedicalActions = false })["finite-native-care"].Ready
                && !Features(new() { EnableClinicalEffects = false })["finite-native-care"].Ready,
                "Report ignored mode or disabled medical/clinical policy.");
        }
        Require(!Features(new())["native-physiology"].Ready && !Features(new())["finite-native-care"].Ready,
            "Unpatched bridges still reported ready.");
        var activity = mod.GetType("Seraph.Server.ActivityCompatibility", true)!;
        var activityStatus = activity.GetMethod("Status", flags)!;
        var pairs = Array.CreateInstance(typeof(ValueTuple<string, string>), 5);
        pairs.SetValue(ValueTuple.Create("jaunt", "3.3.1"), 0);
        pairs.SetValue(ValueTuple.Create("equusferus", "1.0.6"), 1);
        pairs.SetValue(ValueTuple.Create("shipwright", "1.5.1"), 2);
        pairs.SetValue(ValueTuple.Create("walkableentities", "1.0.44"), 3);
        pairs.SetValue(ValueTuple.Create("carryon", "2.0.0-rc.1"), 4);
        var adapters = ((IEnumerable)activityStatus.Invoke(null, [pairs])!).Cast<object>().ToDictionary(
            x => (string)x.GetType().GetProperty("Id")!.GetValue(x)!,
            x => (string)x.GetType().GetProperty("Status")!.GetValue(x)!);
        Require(adapters["jaunt"] == "observe-only" && adapters["equusferus"] == "observe-only"
            && adapters["shipwright"] == "observe-only" && adapters["walkableentities"] == "observe-only"
            && adapters["carryon"] == "native-stats", "Uninstalled adapters overstated readiness or lost native stat support.");
        using (var vehicles = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeVehicleBridge", true)!, true)!)
        {
            var liveAdapters = ((IEnumerable)activityStatus.Invoke(null, [pairs])!).Cast<object>().ToDictionary(
                x => (string)x.GetType().GetProperty("Id")!.GetValue(x)!, x => (string)x.GetType().GetProperty("Status")!.GetValue(x)!);
            Require(liveAdapters["jaunt"] == "observe-only" && liveAdapters["equusferus"] == "hooks-ready" && liveAdapters["shipwright"] == "observe-only",
                "Adapter readiness followed a version string rather than installed hooks.");
            Require(Features(new())["human-vehicle-control"] == (true, 2)
                && !Features(new() { Mode = "observe" })["human-vehicle-control"].Ready
                && !Features(new() { EnableInjuryActionEffects = false })["human-vehicle-control"].Ready,
                "Vehicle feature diagnostics ignored real native hook count or disabled policy.");
        }
        Console.WriteLine("PASS runtime diagnostics: actual hooks, oxygen match, medical policy, observe mode and disposal; no blanket compatibility claim");
    }
}
