using System.Reflection;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal sealed class DownedFixtureEntity : EntityPlayer
{
    public DamageSource? DeathSource;
    public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource? damageSourceForDeath = null)
    { DeathSource = damageSourceForDeath; Alive = false; }
}

internal static class DownedChecks
{
    internal static void Run(Assembly mod, object server, object session, DownedFixtureEntity entity, ICoreServerAPI api)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var package = ZipFile.OpenRead(Path.Combine(root, "compatibility/Downed-2.7.7.zip"));
        using var entry = package.Entries.Single(e => e.Name == "Downed.dll").Open();
        using var bytes = new MemoryStream();
        entry.CopyTo(bytes); bytes.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(bytes);
        var type = assembly.GetType("Downed.EntityBehaviorDowned", true)!;
        var behavior = (EntityBehavior)Activator.CreateInstance(type, [entity])!;
        var externalSystem = Activator.CreateInstance(assembly.GetType("Downed.DownedModSystem", true)!)!;
        var config = externalSystem.GetType().GetProperty("Config")!.GetValue(externalSystem)!;
        var cooldownField = config.GetType().GetField("DownedCooldownIngameHours");
        if (cooldownField is not null) cooldownField.SetValue(config, 1f);
        else config.GetType().GetProperty("DownedCooldownIngameHours")!.SetValue(config, 1f);
        type.GetField("mod", all)!.SetValue(behavior, externalSystem);
        var health = new EntityBehaviorHealth(entity);
        type.GetField("health", all)!.SetValue(behavior, health);
        entity.Properties.Server.Behaviors.Add(behavior);
        entity.Stats.Set("walkspeed", "fixture", 0);
        using var native = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativePhysiologyBridge", true)!, true)!;
        using var adapter = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeDownedBridge", true)!, all, null, [api], null)!;
        var frame = session.GetType().GetProperty("Framework")!;
        FrameworkState Frame() => (FrameworkState)frame.GetValue(session)!;
        void Set(FrameworkState value) => frame.SetValue(session, value);
        var change = server.GetType().GetMethod("ChangeOwnHealth", all)!;
        var sync = server.GetType().GetMethod("SyncDowned", all)!;
        var rescue = adapter.GetType().GetMethod("RescueAfterConsumedTreatment", all)!;
        var damage = new DamageSource { Source = EnumDamageSource.Entity, Type = EnumDamageType.SlashingAttack,
            HitPosition = new(0, 1.7, 0) };
        Set(FrameworkState.Create("downed-head"));
        Require((float)type.GetMethod("OnDamaged", all)!.Invoke(behavior, [21f, damage])! == 21,
            "Downed clipped the regional damage before tissue accounting.");
        change.Invoke(server, [entity, 79f, damage]);
        Require(entity.Alive && entity.WatchedAttributes.GetBool("downed") && Frame().Downed.Active,
            "Lethal head damage bypassed Downed.");
        Require(Frame().Vitals.Integrity[0] == 0 && Frame().Wounds.Single().IntegrityDamage == 20,
            "Lethal head damage fabricated whole-body tissue loss.");
        Require(ReferenceEquals(type.GetField("lastDamageSource", all)!.GetValue(behavior), damage),
            "Downed lost the original damage source.");
        Require(health.Health > 0 && health.Health < 1 && Frame().Vitals.Health == 0,
            "Downed floor was not a projection of the lethal ledger.");
        var retained = Frame();
        health.Health = .05f;
        Require(Frame().Vitals == retained.Vitals && Frame().Wounds == retained.Wounds,
            "Downed floor write changed regional injury.");
        Set(JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(Frame()))!);
        sync.Invoke(server, [entity]);
        Require(Frame().Vitals.Integrity[0] == 0 && Frame().Downed.Active && health.Health > 0,
            "Reload granted regional recovery or lost downed projection.");
        Require(!(bool)type.GetMethod("ReviveByHealing")!.Invoke(behavior, [entity])!,
            "Target lookup revived before completed item consumption.");
        Require((float)type.GetMethod("OnDamaged", all)!.Invoke(behavior, [10f, damage])! == 0,
            "Downed damage protection failed.");
        health.Health = 20; // Downed passed-out animation must not heal all regions.
        Require(Frame().Vitals.Integrity[0] == 0, "Passed-out projection became real healing.");
        var wounds = Frame().Wounds;
        using var bandages = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeBandageBridge", true)!, true)!;
        var item = new Item { Code = new("fixture:healing-item"), ItemId = 999 };
        var slot = new DummySlot(new ItemStack(item, 2));
        var use = Activator.CreateInstance(bandages.GetType().GetNestedType("Use", all)!, all, null,
            [entity, slot, item, 2, 0L, false, false, 1d, null, entity], null)!;
        var complete = bandages.GetType().GetMethod("Complete", all)!;
        complete.Invoke(null, [use]);
        Require(Frame().Downed.Active, "Cancelled/unconsumed healing item revived the player.");
        slot.Itemstack!.StackSize = 1;
        ((FixturePlayer)entity.World.PlayerByUid(entity.WatchedAttributes.GetString("playerUID"))).FailSave = true;
        complete.Invoke(null, [use]);
        Require(Frame().Downed.Active && slot.StackSize == 2, "Failed checkpoint consumed rescue or revived the player.");
        ((FixturePlayer)entity.World.PlayerByUid(entity.WatchedAttributes.GetString("playerUID"))).FailSave = false;
        use = Activator.CreateInstance(bandages.GetType().GetNestedType("Use", all)!, all, null,
            [entity, slot, item, 2, 0L, false, false, 1d, null, entity], null)!;
        slot.Itemstack.StackSize = 1;
        complete.Invoke(null, [use]);
        Require(!Frame().Downed.Active, "Completed consumed treatment did not delegate rescue.");
        Require(!Frame().Downed.Active && Frame().Vitals.Integrity[0] == 4 && Frame().Vitals.Integrity[1] == 20
            && ReferenceEquals(Frame().Wounds, wounds), "Rescue reset wounds or failed the per-region 20% floor.");
        var rescued = Frame();
        Require(!(bool)rescue.Invoke(null, [entity, entity])! && Frame() == rescued, "Rescue was awarded twice.");
        Set(Frame() with { Vitals = Frame().Vitals.Change(-100) });
        server.GetType().GetMethod("CheckOwnDeath", all)!.Invoke(server, [entity]);
        Require(!entity.Alive && !Frame().Downed.Active, "Native Downed cooldown was bypassed.");
        entity.Alive = true; entity.WatchedAttributes.RemoveAttribute("lastDownedTotalHours");
        // Ongoing physiological damage has no native onDamaged callback.
        Set(Frame() with { Vitals = Frame().Vitals.Change(-100) });
        entity.WatchedAttributes.RemoveAttribute("hunger");
        server.GetType().GetMethod("ProjectPhysiology", all)!.Invoke(server, [entity]);
        Require(entity.Alive && Frame().Downed.Active, "Clinical lethal damage bypassed Downed.");
        type.GetMethod("GiveUp")!.Invoke(behavior, null);
        Require(!entity.Alive && entity.WatchedAttributes.GetBool("downeddied"), "Downed give-up did not retain death authority.");
        // Without a compatible behavior the original death path must remain available.
        entity.Alive = true; entity.Properties.Server.Behaviors.Remove(behavior);
        Set(Frame() with { Downed = new() });
        server.GetType().GetMethod("CheckOwnDeath", all)!.Invoke(server, [entity]);
        Require(!entity.Alive, "Missing Downed created immortality.");
        Console.WriteLine("PASS real Downed 2.7.7 regional/clinical entry, source attribution, floor projection, reload, delayed rescue, 20% recovery and native death fallback");
    }
}
