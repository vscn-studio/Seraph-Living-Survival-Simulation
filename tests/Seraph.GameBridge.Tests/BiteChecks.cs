using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

internal static class BiteChecks
{
    internal static void Run(Assembly mod)
    {
        var method = mod.GetType("Seraph.Server.ServerCoordinator", true)!
            .GetMethod("VerifiedBite", BindingFlags.NonPublic | BindingFlags.Static)!;
        var animations = new Dictionary<string, AnimationMetaData> { ["attack-withwindup"] = new() };
        var animal = new EntityAgent { Code = new("game:wolf-male") };
        animal.AnimManager = (IAnimationManager)RoutingProxy.Make(typeof(IAnimationManager), (m, _) =>
            m.Name == "get_ActiveAnimationsByAnimCode" ? animations : throw new Exception(m.Name));
        var source = new DamageSource { Source = EnumDamageSource.Entity, Type = EnumDamageType.SlashingAttack, SourceEntity = animal };
        string? Read() => (string?)method.Invoke(null, [source]);
        void Check(bool condition) { if (!condition) throw new Exception("Unverified animal attack treated as infectious bite."); }
        Check(Read() == "pasteurella");
        animations.Clear(); Check(Read() is null);
        animations["attack-withwindup"] = new();
        animal.Code = new("game:bear-brown"); Check(Read() is null);
        animal.Code = new("game:boar-male"); Check(Read() is null);
        animal.Code = new("other:wolf-male"); Check(Read() is null);
        animal.Code = new("game:wolf-male"); source.Type = EnumDamageType.BluntAttack; Check(Read() is null);
        Console.WriteLine("PASS animal infection requires confirmed native canine attack; inactive attacks, claws, tusks and unknown sources rejected");
    }
}
