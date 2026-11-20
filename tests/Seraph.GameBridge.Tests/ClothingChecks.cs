using System.Reflection;
using Seraph;
using Seraph.Application;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

internal static class ClothingChecks
{
    internal static void Run()
    {
        var method = typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.MicroclimateAdapter", true)!
            .GetMethod("Clothing", BindingFlags.Static | BindingFlags.NonPublic)!;
        var shadeMethod = method.DeclaringType!.GetMethod("HeadgearShade", BindingFlags.Static | BindingFlags.NonPublic)!;
        (double Clo, int Mapped) Sample(ItemSlot[] slots, SeraphServerConfig? config = null)
            => ((double, int))method.Invoke(null, [slots, config ?? new()])!;
        void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        var shirt = new Item { Code = new("game:clothes-upperbody-shirt"), Attributes = JsonObject.FromJson("{\"warmth\":4,\"clothescategory\":\"UpperBody\"}") };
        var stack = new ItemStack(shirt);
        stack.Attributes.SetFloat("condition", 1);
        var slot = new DummySlot(stack);
        Check(Sample([]).Clo == 0, "Naked character received unknown-clothing fallback.");
        Check(Math.Abs(Sample([slot]).Clo - .4) < 1e-9, "Native clothes have no insulation.");
        stack.Attributes.SetFloat("condition", .25f);
        var wearable = new CollectibleBehaviorWearable(shirt);
        Check(Math.Abs(Sample([slot]).Clo - wearable.GetWarmth(slot) * .1) < 1e-9, "Condition mapping differs from native warmth.");
        Check(Sample([slot], new() { ClothingCloByCode = new() { [shirt.Code.ToString()] = .8 } }).Clo == .8,
            "Explicit clothing override lost priority.");
        stack.Attributes.RemoveAttribute("condition");
        Check(Math.Abs(Sample([slot]).Clo - .4) < 1e-9 && !stack.Attributes.HasAttribute("condition"), "Sampling initialized or randomized clothing condition.");
        var unknown = new DummySlot(new ItemStack(new Item { Code = new("modded:shirt"), Attributes = JsonObject.FromJson("{\"clothescategory\":\"UpperBody\"}") }));
        Check(Sample([unknown, unknown]).Clo == .6, "Unknown garment fallback stacks per item.");
        var armor = new DummySlot(new ItemStack(new Item { Code = new("modded:armor"), Attributes = JsonObject.FromJson("{\"clothescategory\":\"ArmorBody\",\"warmth\":20}") }));
        Check(Sample([armor]).Clo == 0, "Armor incorrectly counted as insulating clothes.");
        var trinket = new DummySlot(new ItemStack(new Item { Code = new("modded:trinket") }));
        Check(Sample([trinket]).Clo == 0, "Unrelated character item gives clothing insulation.");
        var hat = new DummySlot(new ItemStack(new Item { Code = new("modded:hat"),
            Attributes = JsonObject.FromJson("{\"clothescategory\":\"Head\",\"shade\":0.6}") }));
        var mappedHat = new DummySlot(new ItemStack(new Item { Code = new("modded:mapped-hat"),
            Attributes = JsonObject.FromJson("{\"clothescategory\":\"Head\"}") }));
        double Shade(ItemSlot[] slots, SeraphServerConfig? config = null)
            => (double)shadeMethod.Invoke(null, [slots, config ?? new()])!;
        Check(Math.Abs(Shade([hat]) - .6) < 1e-9, "Headgear shade attribute was ignored.");
        Check(Math.Abs(Shade([mappedHat], new() { HeadgearShadeByCode = new() { ["modded:mapped-hat"] = .8 } }) - .8) < 1e-9,
            "Explicit headgear shade mapping was ignored.");
        Check(Shade([trinket]) == 0, "Unrelated item received headgear shade.");
        Console.WriteLine("PASS native clothing warmth/condition, exact overrides, naked character, unknown garment fallback, armor exclusion and headgear shade");
    }
}
