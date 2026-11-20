using System.Reflection;
using Seraph.Core.Framework;
using Vintagestory.API.Common;

internal static class LaborChecks
{
    internal static void Run(Assembly mod, object server, object session, EntityPlayer entity, FixturePlayer player)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var frame = session.GetType().GetProperty("Framework")!;
        var original = (FrameworkState)frame.GetValue(session)!;
        var initial = FrameworkState.Create(player.PlayerUID);
        frame.SetValue(session, initial);
        FrameworkState Current() => (FrameworkState)frame.GetValue(session)!;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var held = server.GetType().GetMethod("RecordHeldWork", flags)!;
        var end = server.GetType().GetMethod("EndHeldWork", flags)!;
        var mining = mod.GetType("Seraph.Server.NativeInjuryActionBridge", true)!.GetMethod("MiningEnd", flags)!;
        mining.Invoke(null, [player, 10f, 10f, .5f]);
        Require(Current().Stamina == initial.Stamina, "Rejected mining charged labor.");
        mining.Invoke(null, [player, 10f, 9f, .5f]);
        Require(Current().Stamina < initial.Stamina, "Accepted mining did not charge labor.");
        held.Invoke(server, [entity, "fixture", .5d, ExertionKind.Crafting]);
        var paid = Current().Stamina;
        held.Invoke(server, [entity, "fixture", .5d, ExertionKind.Crafting]);
        Require(Current().Stamina == paid, "Held work replay charged twice.");
        end.Invoke(server, [entity, "fixture", 0d, ExertionKind.Standing]);
        held.Invoke(server, [entity, "fixture", .5d, ExertionKind.Crafting]);
        Require(Current().Stamina < paid, "Held work did not restart after cancellation.");
        frame.SetValue(session, Current() with { ExertionExhausted = true });
        paid = Current().Stamina;
        held.Invoke(server, [entity, "fixture", 1d, ExertionKind.Crafting]);
        Require(Current().Stamina == paid, "Exhaustion allowed active labor.");
        end.Invoke(server, [entity, "fixture", 0d, ExertionKind.Standing]);
        frame.SetValue(session, original);
        Console.WriteLine("PASS accepted/rejected native mining, held replay/cancel restart and authoritative exhaustion labor gate");
    }
}
