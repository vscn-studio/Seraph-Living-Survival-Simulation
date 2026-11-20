using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Seraph;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;

internal static class BleedingChecks
{
    private sealed class LocatedDamageSource(string region) : DamageSource, IVerifiedInjuryLocation
    { public string InjuryRegion => region; }
    private sealed class FixedRandom : Random
    {
        public double Value = .99;
        public override double NextDouble() => Value;
    }
    internal static void Run(Assembly mod, bool bandageOnly = false)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var entity = new DownedFixtureEntity { Alive = true };
        var player = FixturePlayer.Make(entity, null!);
        entity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        var random = new FixedRandom();
        var logger = Proxy<ILogger>((m, _) => null);
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_TotalHours" ? 10d : throw new Exception(m.Name));
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "PlayerByUid" => player, "get_Rand" => random,
            "get_Logger" => logger, "get_Calendar" => calendar, "get_ElapsedMilliseconds" => 100000L, _ => throw new Exception(m.Name)
        });
        var system = new SeraphModSystem();
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? system : throw new Exception(m.Name));
        var save = RoutingProxy.Make(typeof(IWorldManagerAPI).GetProperty("SaveGame")!.PropertyType,
            (m, _) => m.Name == "get_SavegameIdentifier" ? "bleeding-world" : throw new Exception(m.Name));
        var manager = Proxy<IWorldManagerAPI>((m, _) => m.Name == "get_SaveGame" ? save : throw new Exception(m.Name));
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_WorldManager" => manager, "get_World" => world, "get_ModLoader" => loader, "get_Logger" => logger, _ => throw new Exception(m.Name)
        });
        entity.Api = api; entity.World = world;
        var serverType = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(serverType, [api])!;
        serverType.GetField("capabilities", flags)!.SetValue(server, CapabilityRegistry.Create(new(), []));
        typeof(SeraphModSystem).GetField("server", flags)!.SetValue(system, server);
        var sessionType = serverType.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        ((IDictionary)serverType.GetField("sessions", flags)!.GetValue(server)!).Add(player.PlayerUID, session);
        var frame = new FrameworkKernel().AddWound(FrameworkState.Create(player.PlayerUID), WoundKind.Cut, .3, .001).State;
        sessionType.GetProperty("Framework")!.SetValue(session, frame);
        if (bandageOnly)
        {
            NativeCareChecks.Run(mod, server, session, entity, player);
            Console.WriteLine("PASS isolated native bandage/hemostasis checks");
            return;
        }
        var visual = mod.GetType("Seraph.Server.BleedingVisualBridge", true)!;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var externalPath = Path.Combine(root, "artifacts/api-bleeding/BloodTrail-1.2.12/BloodTrail.dll");
        if (!File.Exists(externalPath)) throw new Exception("Missing local BloodTrail compatibility fixture.");
        var external = AssemblyLoadContext.Default.LoadFromAssemblyPath(externalPath);
        var behavior = (EntityBehavior)Activator.CreateInstance(external.GetType("BloodTrail.src.Server.EntityBleedingBehavior", true)!, [entity])!;
        typeof(Entity).GetProperty("Properties")!.SetValue(entity, new EntityProperties { Server = new EntityServerProperties([], new()) });
        entity.SidedProperties.Behaviors.Add(behavior);
        using var bridge = (IDisposable)Activator.CreateInstance(visual, nonPublic: true)!;
        var allow = visual.GetMethod("AllowExternal", flags)!;
        if ((bool)allow.Invoke(null, [behavior])!) throw new Exception("External player damage was not suppressed.");
        if ((long)serverType.GetMethod("SelectBandageWound", flags)!.Invoke(server, [entity])! == 0)
            throw new Exception("Optional bleeding behavior disabled native bandages.");
        // The actual external StartBleeding/OnEntityReceiveDamage must return before accessing its config or scheduling damage.
        behavior.GetType().GetMethod("StartBleeding", flags)!.Invoke(behavior, null);
        float damage = 1;
        behavior.OnEntityReceiveDamage(new DamageSource(), ref damage);
        if (damage != 1) throw new Exception("External bleed modified the original hit.");
        visual.GetMethod("Sync", flags)!.Invoke(null, [entity, true]);
        if (!entity.WatchedAttributes.GetBool("isBleeding")) throw new Exception("Particle state was not enabled.");
        sessionType.GetProperty("Framework")!.SetValue(session, WoundCare.Bandage(frame, frame.Wounds[0].EventSequence, false));
        if ((double)serverType.GetMethod("ClinicalBleeding", flags)!.Invoke(server, [session, null, null])! != 0)
            throw new Exception("Native clean bandage left continuing blood loss.");
        visual.GetMethod("Sync", flags)!.Invoke(null, [entity, false]);
        if (entity.WatchedAttributes.GetBool("isBleeding")) throw new Exception("Bandaging left particle bleeding enabled.");
        behavior.entity = new EntityAgent();
        if (!(bool)allow.Invoke(null, [behavior])!) throw new Exception("Animal bleeding was suppressed.");
        CheckClientParticles(visual, external);
        NativeCareChecks.Run(mod, server, session, entity, player);
        LaborChecks.Run(mod, server, session, entity, player);
        // Exercise actual server protection before isolating classification from fat/dodge below.
        sessionType.GetProperty("Framework")!.SetValue(session, FrameworkState.Create("combat-protection"));
        sessionType.GetProperty("State")!.SetValue(session, BodyState.Create() with {
            FatEnergyKJ = DietaryMetabolism.FatKJPerG * 1000 * 10 });
        var protect = serverType.GetMethod("ApplyCombatProtection", flags)!;
        float Protected(EnumDamageType kind)
        {
            var args = new object?[] { entity, new DamageSource { Type = kind, Source = EnumDamageSource.Entity }, 10f };
            protect.Invoke(server, args); return (float)args[2]!;
        }
        if (Protected(EnumDamageType.BluntAttack) != 10
            || Math.Abs((float)serverType.GetMethod("ApplyFatProtection", flags)!.Invoke(server,
                [entity, new DamageSource { Type = EnumDamageType.BluntAttack }, 10f])! - 9) > 1e-5)
            throw new Exception("Actual combat hit omitted 10 kg fat protection.");
        var messages = player.SentMessages.Count;
        random.Value = 0;
        if (Protected(EnumDamageType.BluntAttack) != 0 || Protected(EnumDamageType.Gravity) != 10)
            throw new Exception("Actual dodge failed or incorrectly protected a fall.");
        if (player.SentMessages.Count != messages + 1) throw new Exception("Dodge must issue exactly one missed notification.");
        random.Value = .99;
        sessionType.GetProperty("State")!.SetValue(session, BodyState.Create() with { FatEnergyKJ = 0 });
        using (var actions = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativeInjuryActionBridge", true)!, true)!)
        {
            var breath = new Vintagestory.GameContent.EntityBehaviorBreathe(entity);
            breath.GetType().GetField("oxygenTree", flags)!.SetValue(breath, new TreeAttribute());
            breath.MaxOxygen = 40000; breath.Oxygen = 20000; breath.HasAir = true;
            entity.Properties.Server.Behaviors.Add(breath);
            var apply = serverType.GetMethod("ApplyExertion", flags)!;
            apply.Invoke(server, [session, ExertionKind.Attack, 1d, false, null]);
            if (breath.Oxygen != 20000) throw new Exception("Airborne attack drained native oxygen.");
            apply.Invoke(server, [session, ExertionKind.Standing, 1d, true, null]);
            if (breath.Oxygen != 20000) throw new Exception("Action/recovery receipts duplicated breathing.");
            serverType.GetMethod("RecoverOxygen", flags)!.Invoke(server, [session, 1d]);
            if (Math.Abs(breath.Oxygen - (20000 + 40000d / 45)) > .01) throw new Exception("Continuous native breathing missing.");
            var rate = actions.GetType().GetMethod("RecoveryRate", flags)!;
            if ((float)rate.Invoke(null, [10000f, breath])! != 0) throw new Exception("Native rapid recovery cancels the replacement ledger.");
            breath.Oxygen = 20000;
            breath.OnGameTick(.1f);
            if (breath.Oxygen != 20000) throw new Exception("Patched native tick still rapidly refills oxygen.");
            breath.HasAir = false;
            breath.OnGameTick(.1f);
            if (Math.Abs(breath.Oxygen - 19900) > .01) throw new Exception("Patched native tick changed underwater/smoke depletion.");
            breath.HasAir = true;
            var settings = serverType.GetField("settings", flags)!;
            var originalSettings = settings.GetValue(server);
            foreach (var config in new[] { new SeraphServerConfig { EnableInjuryActionEffects = false }, new SeraphServerConfig { Mode = "observe" } })
            {
                settings.SetValue(server, new SettingsAuthority(config));
                breath.Oxygen = 20000; breath.OnGameTick(.1f);
                if (breath.Oxygen != 21000) throw new Exception("Disabled/observe oxygen ownership still suppressed native refill.");
            }
            settings.SetValue(server, originalSettings);
            breath.Oxygen = 2000;
            apply.Invoke(server, [session, ExertionKind.Attack, 1d, false, null]);
            if (breath.Oxygen != 2000) throw new Exception("Exercise raised or drained an existing smoke oxygen deficit below the air floor.");
            entity.Properties.Server.Behaviors.Remove(breath);
        }
        CheckInjuries(mod, server, session, entity);
        DownedChecks.Run(mod, server, session, entity, api);
        Console.WriteLine("PASS optional BloodTrail player hooks, native bandage ownership, complete hemostasis, watched particle start/stop and animal isolation");
    }

    private static void CheckInjuries(Assembly mod, object server, object session, EntityPlayer entity)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var serverType = server.GetType();
        var state = session.GetType().GetProperty("Framework")!;
        var kernel = new FrameworkKernel(new() { IntegrateBleeding = true });
        serverType.GetField("frameworkKernel", all)!.SetValue(server, kernel);
        FrameworkState Frame() => (FrameworkState)state.GetValue(session)!;
        var health = new Vintagestory.GameContent.EntityBehaviorHealth(entity);
        entity.WatchedAttributes.SetAttribute("health", new TreeAttribute());
        using var native = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Server.NativePhysiologyBridge", true)!, true)!;
        var bridge = native.GetType();
        var begin = bridge.GetMethod("BeginDamage", all)!;
        var end = bridge.GetMethod("EndDamage", all)!;
        void Hit(DamageSource source, float damage)
        {
            var args = new object?[] { health, source, damage, null };
            begin.Invoke(null, args);
            try { health.Health -= (float)args[2]!; }
            finally { end.Invoke(null, [null, args[3]]); }
        }
        foreach (var (region, expectedRoll) in new[] { ("head-neck", .1), ("chest", .4), ("left-hand", .4), ("right-foot", .85) })
        {
            state.SetValue(session, FrameworkState.Create("armor-test"));
            var source = new LocatedDamageSource(region) { Type = EnumDamageType.BluntAttack, Source = EnumDamageSource.Entity };
            var args = new object?[] { health, source, 10f, null }; begin.Invoke(null, args);
            try
            {
                var roll = (double)bridge.GetMethod("ArmorRoll", all)!.Invoke(null, [new FixedRandom { Value = .99 }, ((FixturePlayer)entity.Player), source])!;
                if (roll != expectedRoll) throw new Exception("Armor selected a different region from the HP/wound context.");
            }
            finally { end.Invoke(null, [null, args[3]]); }
        }
        foreach (var (damage, expected, bleed) in new[] {
            (EnumDamageType.SlashingAttack, WoundKind.Cut, true),
            (EnumDamageType.PiercingAttack, WoundKind.Puncture, true),
            (EnumDamageType.BluntAttack, WoundKind.Contusion, false),
            (EnumDamageType.Gravity, WoundKind.Contusion, false),
            (EnumDamageType.Crushing, WoundKind.Contusion, false),
            (EnumDamageType.Fire, WoundKind.Burn, false), (EnumDamageType.Heat, WoundKind.Burn, false),
            (EnumDamageType.Frost, WoundKind.Frostbite, false),
            (EnumDamageType.Electricity, WoundKind.ElectricBurn, false),
            (EnumDamageType.Poison, WoundKind.PoisonWound, false), (EnumDamageType.Acid, WoundKind.PoisonWound, false) })
        {
            state.SetValue(session, FrameworkState.Create("injury-test"));
            Hit(new DamageSource { Type = damage, Source = EnumDamageSource.Entity }, .5f);
            var frame = Frame();
            if (frame.Wounds.Length == 0 || frame.Wounds.Any(w => w.Kind != expected)
                || frame.Wounds.Any(w => w.BleedLps > 0) != bleed)
                throw new Exception($"Native {damage} misclassified wounds/bleeding.");
            if ((frame.AdrenalineLevel > 0) != bleed)
                throw new Exception($"Minor {damage} failed actual-bleeding adrenaline rule.");
            if (Math.Abs(health.Health - 99.5) > 1e-5) throw new Exception("Native damage was paid twice.");
            var metrics = (Seraph.Network.PanelMetric[])serverType.GetMethod("RecoveryMetrics", all)!.Invoke(server, [session])!;
            if (metrics.Count(m => m.Key.StartsWith("wound-")) != frame.Wounds.Length)
                throw new Exception("Classified wound missing from ordinary C-panel metrics.");
            if (bleed)
            {
                var bandaged = WoundCare.Bandage(frame, frame.Wounds[0].EventSequence, false);
                state.SetValue(session, bandaged);
                var rate = (double)serverType.GetMethod("ClinicalBleeding", all)!.Invoke(server, [session, null, null])!;
                if (rate != 0) throw new Exception("Bandaged wound still counted as active bleeding.");
            }
        }
        foreach (var source in new DamageSource?[] { null,
            new() { Type = EnumDamageType.Hunger } })
        {
            state.SetValue(session, FrameworkState.Create("systemic-test"));
            if (source is null) health.Health -= 3;
            else Hit(source, 3);
            if (Frame().Wounds.Length != 0 || Frame().AdrenalineLevel != 0 || Math.Abs(health.Health - 97) > 1e-5)
                throw new Exception("Systemic/no-source health loss fabricated trauma or adrenaline.");
        }
        foreach (var kind in new[] { EnumDamageType.Suffocation, EnumDamageType.Injury })
        {
            state.SetValue(session, FrameworkState.Create("systemic-injury"));
            Hit(new DamageSource { Type = kind, Source = EnumDamageSource.Internal }, 3);
            if (Frame().Wounds.Length != 0
                || Frame().AdrenalineLevel != 0 || Math.Abs(health.Health - 97) > 1e-5)
                throw new Exception("Internal systemic loss fabricated a wound/adrenaline or double damage.");
        }
        state.SetValue(session, FrameworkState.Create("major-hit"));
        Hit(new DamageSource { Type = EnumDamageType.BluntAttack, Source = EnumDamageSource.Entity }, 2);
        if (Frame().AdrenalineRemainingSeconds != 60) throw new Exception("Meaningful nonbleeding hit did not activate adrenaline.");
        state.SetValue(session, FrameworkState.Create("unlocated-melee"));
        Hit(new DamageSource { Type = EnumDamageType.SlashingAttack, Source = EnumDamageSource.Entity }, 1);
        if (Frame().Wounds.Length != 1 || Frame().Wounds[0].Evidence != InjuryEvidence.GameplayDistribution
            || Math.Abs(Frame().Vitals.Integrity.Sum() - 99) > 1e-8
            || Enumerable.Range(0, 6).Count(i => Frame().Vitals.Integrity[i] < VitalState.MaximumFor(i)) != 1)
            throw new Exception("Missing coordinates did not produce one explicitly estimated primary region.");
        entity.SetSelectionBox(.6f, 1.8f); entity.BodyYawServer = 0;
        foreach (var (region, x, y, index) in new[] { ("left-hand", -.29, .9, 2), ("right-hand", .29, .9, 3),
            ("left-foot", -.12, .08, 4), ("right-foot", .12, .08, 5) })
        {
            state.SetValue(session, FrameworkState.Create(region));
            Hit(new DamageSource { Type = EnumDamageType.SlashingAttack, Source = EnumDamageSource.Entity,
                HitPosition = new(x, y, 0) }, 1);
            if (Frame().Wounds.Single().Region != region || Frame().Vitals.Integrity[index] != 14
                || Enumerable.Range(0, 6).Where(i => i != index).Any(i => Frame().Vitals.Integrity[i] != VitalState.MaximumFor(i)))
                throw new Exception($"Located {region} damaged the wrong or multiple body pools.");
        }
        state.SetValue(session, FrameworkState.Create("fall"));
        Hit(new DamageSource { Type = EnumDamageType.Gravity }, 2);
        if (Frame().Vitals.Integrity[4] != 14 || Frame().Vitals.Integrity[5] != 14 || Frame().Wounds.Length != 2)
            throw new Exception("Fall no longer distributes explicitly to both legs.");
        Console.WriteLine("PASS native injury routing: one estimated melee location, four isolated hand/foot hits, and bilateral fall damage");
        Console.WriteLine("PASS native health/source capture: eleven wound mappings, systemic isolation, single damage, effective bleeding, adrenaline and ordinary wound rows");
    }

    private static void CheckClientParticles(Type visual, Assembly external)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> route) => (T)RoutingProxy.Make(typeof(T), route);
        var definition = new EntityProperties { Code = new("game:player"), Client = new EntityClientProperties([], null!) };
        var loader = Proxy<IModLoader>((m, _) => m.Name == "IsModEnabled" ? true : throw new Exception(m.Name));
        var authoritativeWorld = Proxy<IServerWorldAccessor>((m, _) => m.Name == "get_EntityTypes"
            ? new List<EntityProperties> { definition } : throw new Exception(m.Name));
        var serverApi = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_World" => authoritativeWorld, "get_ModLoader" => loader,
            _ => throw new Exception(m.Name)
        });
        visual.GetMethod("AttachParticles", all)!.Invoke(null, [serverApi]);
        visual.GetMethod("AttachParticles", all)!.Invoke(null, [serverApi]);
        if (definition.Client.BehaviorsAsJsonObj.Count(b => b["code"].AsString() == "EntityBleedingBehaviorParticles") != 1)
            throw new Exception("Authoritative player definition missed or duplicated BloodTrail particles.");

        var entity = new EntityPlayer { Alive = true };
        Action<float>? tick = null;
        var spawned = 0; var scheduled = 0; var stopped = 0;
        var air = new Block { Code = new("game:air") };
        var blocks = Proxy<IBlockAccessor>((m, _) => m.Name == "GetBlock" ? air : throw new Exception(m.Name));
        var world = Proxy<IClientWorldAccessor>((m, args) => m.Name switch {
            "RegisterGameTickListener" => Register((Action<float>)args![0]!),
            "UnregisterGameTickListener" => Stop(), "SpawnParticles" => Spawn(),
            "get_BlockAccessor" => blocks, _ => throw new Exception(m.Name)
        });
        long Register(Action<float> callback) { tick = callback; scheduled++; return 7L; }
        object? Stop() { stopped++; return null; }
        object? Spawn() { spawned++; return null; }
        var clientApi = Proxy<ICoreClientAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Side" => EnumAppSide.Client, _ => throw new Exception(m.Name)
        });
        entity.Api = clientApi; entity.World = world;
        var config = Activator.CreateInstance(external.GetType("BloodTrail.src.Config.ConfigClient", true)!, [clientApi, null])!;
        config.GetType().GetProperty("AffectedByRain")!.SetValue(config, false);
        external.GetType("BloodTrail.Core", true)!.GetProperty("ConfigClient")!.SetValue(null, config);
        using var clientBridge = (IDisposable)Activator.CreateInstance(visual, all, null, [true], null)!;
        // Network state can arrive before the behavior initializes (joining an injured player).
        entity.WatchedAttributes.SetBool("isBleeding", true);
        var particles = (EntityBehavior)Activator.CreateInstance(external.GetType("BloodTrail.src.Client.EntityBleedingBehaviorParticles", true)!, [entity])!;
        particles.Initialize(definition, JsonObject.FromJson("{}"));
        if (scheduled != 1 || tick is null) throw new Exception("Already bleeding client failed to start particle timer.");
        tick(.08f);
        if (spawned == 0) throw new Exception("Actual BloodTrail client tick emitted no particles.");
        entity.WatchedAttributes.SetBool("isBleeding", false);
        if (stopped != 1) throw new Exception("Hemostasis did not stop actual client particle timer.");
        entity.WatchedAttributes.SetBool("isBleeding", true);
        if (scheduled != 2) throw new Exception("New bleeding did not restart client particles.");
        particles.OnEntityDespawn(new EntityDespawnData());
        if (stopped != 2) throw new Exception("Despawn left the particle timer active.");
        Console.WriteLine("PASS authoritative particle attachment, actual BloodTrail initialization, emission, hemostasis, restart and despawn");
    }
}
