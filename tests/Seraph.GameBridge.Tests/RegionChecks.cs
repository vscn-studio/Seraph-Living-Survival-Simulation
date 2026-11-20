using System.Reflection;

internal static class RegionChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly mod)
    {
        var region = mod.GetType("Seraph.Client.ScreenRegion", true)!;
        var regions = mod.GetType("Seraph.Client.SymptomRegions", true)!;
        object Rect(int x, int y, int w, int h) => Activator.CreateInstance(region, [x, y, w, h])!;
        var holes = Array.CreateInstance(region, 3);
        holes.SetValue(Rect(20, 20, 30, 30), 0); holes.SetValue(Rect(40, 30, 40, 40), 1); holes.SetValue(Rect(-10, 90, 30, 30), 2);
        var outside = (System.Collections.IEnumerable)regions.GetMethod("Outside", All)!.Invoke(null, [100, 100, holes])!;
        var coverage = new int[100,100];
        foreach (var r in outside)
        {
            int Get(string name) => (int)region.GetProperty(name)!.GetValue(r)!;
            for (int x = Get("X"); x < Get("X") + Get("Width"); x++)
            for (int y = Get("Y"); y < Get("Y") + Get("Height"); y++) coverage[x,y]++;
        }
        for (var x = 0; x < 100; x++) for (var y = 0; y < 100; y++)
        {
            var hole = x >= 20 && x < 50 && y >= 20 && y < 50 || x >= 40 && x < 80 && y >= 30 && y < 70 || x < 20 && y >= 90;
            if (coverage[x,y] != (hole ? 0 : 1)) throw new Exception("Menu exclusion left a gap, overlap or obscured menu pixel.");
        }
        var post = mod.GetType("Seraph.Client.SymptomPostProcess", true)!;
        var copy = post.GetMethod("NeedsSceneCopy", All)!;
        bool Copy(double blur, double closure, double tint, double night) => (bool)copy.Invoke(null, [blur, closure, tint, night])!;
        if (Copy(0, 0, 0, 0) || Copy(1, 1, 1, 1) || Copy(0, .5, 0, 0)
            || !Copy(.075, 0, 0, 0) || !Copy(0, 0, .2, 0) || !Copy(0, 0, 0, .2))
            throw new Exception("Scene-copy routing changed dimming, full closure or clinical/night effects.");
        Console.WriteLine("PASS menu exclusion coverage and no-copy dimming/eyelids/full closure routing");
    }
}
