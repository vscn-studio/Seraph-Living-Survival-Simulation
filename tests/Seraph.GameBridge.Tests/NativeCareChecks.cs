using System.Reflection;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

internal static class NativeCareChecks
{
    internal static void Run(Assembly mod, object server, object session, EntityPlayer entity, FixturePlayer player)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var type = mod.GetType("Seraph.Server.NativeBandageBridge", true)!;
        var useType = type.GetNestedType("Use", flags)!;
        var complete = type.GetMethod("Complete", flags)!;
        var finish = type.GetMethod("Finish", flags)!;
        var frame = session.GetType().GetProperty("Framework")!;
        var original = new FrameworkKernel().AddWound(FrameworkState.Create(player.PlayerUID), WoundKind.Cut, .3, .001).State;
        FrameworkState Current() => (FrameworkState)frame.GetValue(session)!;
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        foreach (var code in new[] { "game:bandage-clean", "game:bandage-alcoholed", "game:poultice-reed-horsetail" })
        {
            frame.SetValue(session, original);
            var item = new Item { Code = new(code), ItemId = 989 };
            var slot = new DummySlot(new ItemStack(item, 2));
            slot.Itemstack!.Attributes.SetString("fixture", "preserve");
            object Use() => Activator.CreateInstance(useType, flags, null, [entity, slot, item, 2,
                original.Wounds[0].EventSequence, code.EndsWith("alcoholed"), code.Contains("poultice"), 2d, null, entity], null)!;
            var failed = Use();
            player.FailSave = true;
            slot.Itemstack.StackSize = 1;
            complete.Invoke(null, [failed]);
            Require(ReferenceEquals(Current(), original) && slot.StackSize == 2
                && slot.Itemstack.Attributes.GetString("fixture") == "preserve", "Native care failed to roll back item and wound.");
            complete.Invoke(null, [failed]);
            Require(ReferenceEquals(Current(), original) && slot.StackSize == 2, "Failed use replay changed state.");
            player.FailSave = false;
            var accepted = Use();
            complete.Invoke(null, [accepted]);
            Require(ReferenceEquals(Current(), original), "Unconsumed care granted treatment.");
            // Vanilla and other inventory hooks may rebuild the stack object during debit.
            slot.Itemstack = new ItemStack(new Item { Code = new(code), ItemId = item.ItemId }, 1);
            complete.Invoke(null, [accepted]);
            Require(!ReferenceEquals(Current(), original) && slot.StackSize == 1,
                "Consumed native care did not commit when the inventory rebuilt its stack.");
            Require(WoundCare.Rate(Current().Wounds[0]) == 0, "Native treatment failed to stop bleeding immediately.");
            var saved = Current();
            complete.Invoke(null, [accepted]);
            Require(ReferenceEquals(Current(), saved) && slot.StackSize == 1, "Native care replay granted a second effect.");
            var error = new InvalidOperationException("injected native exception");
            Require(ReferenceEquals(finish.Invoke(null, [error, accepted]), error) && slot.StackSize == 1,
                "Post-commit exception refunded a consumed treatment.");
            frame.SetValue(session, original); slot.Itemstack.StackSize = 2;
            var interrupted = Use(); slot.Itemstack.StackSize = 1;
            Require(ReferenceEquals(finish.Invoke(null, [error, interrupted]), error)
                && slot.StackSize == 2 && ReferenceEquals(Current(), original), "Native exception rollback failed.");
        }
        // Exercise the actual native item debit and the Harmony transaction.
        typeof(Entity).GetProperty("Properties")!.SetValue(entity, new EntityProperties { Server = new EntityServerProperties([], new()) });
        using (var bridge = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!)
        {
            foreach (var code in new[] { "game:bandage-clean", "game:poultice-reed-horsetail" })
            {
                frame.SetValue(session, original);
                var item = new Item { Code = new(code), ItemId = 989 };
                var slot = new DummySlot(new ItemStack(item, 1));
                var behavior = new CollectibleBehaviorHealingItem(item) { AffectedByArmor = false, ApplicationTimeSec = 3, AppliedSound = null, CancelInAir = false };
                var handling = EnumHandling.PassThrough;
                var hand = EnumHandHandling.NotHandled;
                behavior.OnHeldInteractStart(slot, entity, null!, null!, true, ref hand, ref handling);
                Require(hand != EnumHandHandling.NotHandled, "Native treatment did not start.");
                behavior.OnHeldInteractStop(2, slot, entity, null!, null, ref handling);
                Require(slot.StackSize == 1 && ReferenceEquals(Current(), original), "Interrupted native use changed treatment or inventory.");
                behavior.OnHeldInteractStop(3, slot, entity, null!, null, ref handling);
                Require(slot.StackSize == 0 && WoundCare.Rate(Current().Wounds[0]) == 0,
                    "Completed native use did not consume the item and stop bleeding.");
            }
        }
        frame.SetValue(session, original);
        Console.WriteLine("PASS native bandages/poultice: cancellation, exact debit, checkpoint rollback with attributes, replay and exception receipts");
    }
}
