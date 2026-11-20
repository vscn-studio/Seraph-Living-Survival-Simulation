using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

internal static class CharacterPanelChecks
{
    internal static void Run(Assembly mod)
    {
        var position = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!
            .GetMethod("PositionNativePanel", BindingFlags.Static | BindingFlags.NonPublic)!;
        var panelType = position.DeclaringType!;
        var panelEnum = panelType.GetNestedType("Panel", BindingFlags.NonPublic)!;
        var statusPanel = Enum.Parse(panelEnum, "Status");
        var statusKeys = (string[])panelType.GetMethod("MetricKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(panelType), [statusPanel])!;
        Require(statusKeys.SequenceEqual(new[] { "bodywater", "musclemass" }), "Body panel retained unsupported diagnostics or lost water/muscle.");
        var metricKeys = panelType.GetMethod("MetricKeys", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(panelType);
        var bloodPanel = Enum.Parse(panelEnum, "BloodCirculation");
        var bloodKeys = (string[])metricKeys.Invoke(instance, [bloodPanel])!;
        foreach (var key in new[] { "blood", "heartrate", "breathrate", "bleeding", "bloodwaste", "bloodtoxicity", "bloodregen", "bloodfiltration", "bloodclearance", "bloodglucose", "lactate" })
            Require(bloodKeys.Contains(key), "Blood circulation panel does not render " + key + ".");
        Require(!bloodKeys.Any(key => key is "spo2" or "bloodpressure"), "Unused vitals remained in circulation.");
        var immunePanel = Enum.Parse(panelEnum, "DiseaseRecovery");
        var immuneKeys = (string[])metricKeys.Invoke(instance, [immunePanel])!;
        Require(immuneKeys.Contains("whitecells"), "White cell reserve is not assigned to the immune system panel.");
        Require(immuneKeys.Contains("pain"), "Pain is missing from the recovery panel.");
        var energyPanel = Enum.Parse(panelEnum, "EnergyBalance");
        var energyKeys = (string[])metricKeys.Invoke(instance, [energyPanel])!;
        Require(energyKeys.SequenceEqual(new[] { "stamina", "fatigue", "sleeppressure" }), "Energy balance lost stamina, fatigue, or sleep pressure.");
        Require(!Enum.GetNames(panelEnum).Contains("Nutrition"), "Nutrition remains a separate panel.");
        var measure = position.DeclaringType!.GetMethod("MeasurePanelHeights", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var width in new[] { 250d, 300, 360 })
        {
            var compact = measure.Invoke(null, [width, 30d, 22d])!;
            var largerFont = measure.Invoke(null, [width, 36d, 28d])!;
            double Height(object sizes, string panel) => (double)sizes.GetType().GetProperty(panel)!.GetValue(sizes)!;
            Require(Height(compact, "Environment") == 34 + 4 * (22 + 6) + 10 + 24, "Environment still occupies twelve single-column rows.");
            var nutrientCount = ((Array)panelType.GetField("DisplayNutrients", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Length;
            var diagramHeight = (double)panelType.GetMethod("DigestionHeight", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [width - 24])!;
            Require(Math.Abs(Height(compact, "Body") - (34 + statusKeys.Length * 30 + 10 + 24 + diagramHeight + 10 + 6 + (3 + nutrientCount) * 30)) < .001,
                "Body height does not fit a separate full-width row for every nutrition/reserve bar.");
            Require(Height(compact, "Disease") < Height(compact, "Body")
                && Height(compact, "Thermoregulation") < Height(compact, "Body")
                && Height(compact, "EnergyBalance") < Height(compact, "Body"), "Content panels are still stretched to a shared height.");
            Require(Height(largerFont, "BloodCirculation") > Height(compact, "BloodCirculation"),
                "Blood circulation panel height did not accommodate larger text.");
            foreach (var panel in new[] { "Environment", "EnergyBalance", "Body", "BloodCirculation", "Functions", "Disease", "Thermoregulation" })
                Require(Height(largerFont, panel) > Height(compact, panel), "Natural panel height did not accommodate larger text.");
        }
        var immuneHeight = position.DeclaringType!.GetMethod("ImmunePanelHeight", BindingFlags.Static | BindingFlags.NonPublic)!;
        double ImmuneHeight(int count) => (double)immuneHeight.Invoke(null, [240d, 30d, count, 900d])!;
        Require(ImmuneHeight(1) == 240 && ImmuneHeight(6) == 390 && ImmuneHeight(80) == 900,
            "Immune window is fixed to four rows or exceeds the other columns.");
        var previousScale = RuntimeEnv.GUIScale;
        try
        {
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                var window = ElementBounds.Fixed(0, 0, 1920 / scale, 1080 / scale);
                window.ParentBounds = ElementBounds.Empty;
                window.CalcWorldBounds();
                var clip = ElementBounds.Fixed(0, 0, 220, 150);
                clip.ParentBounds = window;
                var content = ElementBounds.Fixed(0, 0, 220, 600);
                content.ParentBounds = clip; clip.ChildBounds.Add(content);
                var row = ElementBounds.Fixed(0, 420, 200, 25);
                row.ParentBounds = content; content.ChildBounds.Add(row);
                clip.CalcWorldBounds();
                position.DeclaringType!.GetMethod("PositionImmuneContent", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [content, 400d]);
                Require(Math.Abs(row.renderY / scale - 20) < .001 && clip.fixedHeight == 150,
                    "Scrollable immune text did not follow content at GUI scale.");
                var native = ElementBounds.Fixed(0, 0, 450, 500)
                    .WithAlignment(EnumDialogArea.LeftMiddle).WithFixedAlignmentOffset(20, 0);
                native.ParentBounds = window;
                var model = ElementBounds.Fixed(65, 80, 180, 300);
                model.ParentBounds = native;
                native.ChildBounds.Add(model);
                native.CalcWorldBounds();
                var modelX = model.renderX - native.renderX;
                var modelY = model.renderY - native.renderY;
                foreach (var (left, top) in new[] { (125d, 75d), (250d, 140d), (125d, 75d) })
                {
                    position.Invoke(null, [native, left, top]);
                    Require(Math.Abs(native.renderX / scale - left) < .001
                        && Math.Abs(native.renderY / scale - top) < .001, "Native character panel retained old alignment/cache.");
                    Require(model.renderX - native.renderX == modelX && model.renderY - native.renderY == modelY,
                        "Native character model moved relative to equipment slots.");
                    Require(native.OuterWidth == 450 * scale && native.OuterHeight == 500 * scale,
                        "Moving native panel resized its contents.");
                }
            }
        }
        finally { RuntimeEnv.GUIScale = previousScale; }
        Console.WriteLine("PASS content-sized panel heights, two-column environment, larger font allowance; native bounds move at GUI scales 1/1.5/2 with unchanged model/slots/dimensions");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
