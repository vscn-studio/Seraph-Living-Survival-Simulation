using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    internal bool OwnsPhysiology(Entity entity) => !disposed && settings.Config.Mode == "survival"
        && entity is EntityPlayer { Player: IServerPlayer player } && Current(player, out var s)
        && player.WorldData.CurrentGameMode == EnumGameMode.Survival
        && s.Fault is null && ReferenceEquals(player.Entity, entity);
    internal bool AllowsBedWithoutNativeTiredness(Entity entity) => OwnsPhysiology(entity)
        && SessionFor(entity).State.SleepPressure >= .7;
    private Session SessionFor(Entity entity) => sessions[((EntityPlayer)entity).PlayerUID];
    internal float OwnHealth(Entity entity)
    {
        var s = SessionFor(entity);
        SyncExternalMaximum(s);
        return (float)(s.Deceased ? 0 : IsOwnedDowned(entity) ? s.Framework.Downed.ProjectedHealth : s.Framework.Vitals.Health);
    }
    private static void SyncExternalMaximum(Session s)
    {
        var vitals = s.Framework.Vitals.WithExternalMaximum(NativeStatInputs.ExternalHealth(s.Player.Entity));
        if (!ReferenceEquals(vitals, s.Framework.Vitals)) s.Framework = s.Framework with { Vitals = vitals };
    }
    internal float OwnMaxHealth(Entity entity)
    {
        var s = SessionFor(entity); SyncExternalMaximum(s);
        return (float)s.Framework.Vitals.EffectiveMaximum;
    }
    internal bool IsOwnedDowned(Entity entity) => OwnsPhysiology(entity) && NativeDownedBridge.IsActive(entity);
    internal void CompleteDownedRescue(Entity entity)
    {
        if (!OwnsPhysiology(entity) || !entity.Alive || NativeDownedBridge.IsActive(entity)) return;
        var s = SessionFor(entity);
        if (!s.Framework.Downed.Active) return;
        // Rescue is a bounded recovery, never a respawn or a nutrition/wound reset.
        s.Framework = s.Framework with { Downed = new(), Vitals = s.Framework.Vitals with {
            Integrity = s.Framework.Vitals.Integrity.Select((v, i) => Math.Max(v, VitalState.MaximumFor(i) * .2)).ToArray() } };
        ProjectPhysiology(entity);
        if (channel is not null) Save(s);
    }
    internal void SyncDowned(Entity entity)
    {
        if (!OwnsPhysiology(entity)) return;
        var s = SessionFor(entity);
        var active = NativeDownedBridge.IsActive(entity);
        if (s.Framework.Downed.Active != active)
            s.Framework = s.Framework with { Downed = new() { Active = active,
                ProjectedHealth = active ? NativeDownedBridge.Floor(entity) : .1 } };
    }
    internal void ConfirmEngineRevival(Entity entity)
    {
        var s = SessionFor(entity);
        if (s.Deceased && entity.Alive) { s.PendingRespawn = true; ConfirmRespawn(s); }
    }
    internal void ApplyCombatProtection(Entity entity, DamageSource source, ref float damage)
    {
        if (source.Type is EnumDamageType.Heal or EnumDamageType.Hunger or EnumDamageType.Gravity
            or EnumDamageType.Suffocation or EnumDamageType.Frost or EnumDamageType.Heat or EnumDamageType.Fire
            || source.Source is EnumDamageSource.Internal or EnumDamageSource.Revive or EnumDamageSource.Suicide)
            return;
        var attacker = source.CauseEntity ?? source.SourceEntity;
        if (source.Source is not (EnumDamageSource.Entity or EnumDamageSource.Player)
            && attacker is not EntityAgent && source.SourceEntity is not IProjectile) return;
        var s = SessionFor(entity);
        if (EyesClosed(s)) return; // No facing bonus or adrenaline can restore dodge with closed eyes.
        var functions = EffectiveFunctions(s);
        var chance = functions.Dodge;
        if (attacker is not null)
        {
            var dx = attacker.Pos.X - entity.Pos.X;
            var dz = attacker.Pos.Z - entity.Pos.Z;
            var view = entity.Pos.GetViewVector();
            var length = Math.Sqrt((dx * dx + dz * dz) * (view.X * view.X + view.Z * view.Z));
            if (length > .0005) chance = CombatFunctions.FacingDodge(chance, (dx * view.X + dz * view.Z) / length);
        }
        if (entity.World.Rand.NextDouble() < chance)
        {
            damage = 0;
            s.Player.SendMessage(Vintagestory.API.Config.GlobalConstants.InfoLogChatGroup,
                L(s.Player, "combat-missed"), EnumChatType.Notification);
        }
    }

    internal float ApplyFatProtection(Entity entity, DamageSource source, float damage)
    {
        if (damage <= 0 || source.Type is not (EnumDamageType.SlashingAttack or EnumDamageType.PiercingAttack or EnumDamageType.BluntAttack)
            || source.Source is EnumDamageSource.Internal or EnumDamageSource.Revive or EnumDamageSource.Suicide) return damage;
        return damage * (float)(1 - EffectiveFunctions(SessionFor(entity)).FatDamageReduction);
    }
    internal void RecordCombatDiagnostic(Entity entity, float raw, float shield, float armor, float fat, string slot)
    {
        SessionFor(entity).LastCombatStages = $"raw={raw:0.###}; shield={shield:0.###}; armor={armor:0.###}; fat={fat:0.###}; slot={slot}";
    }

    internal float ChangeOwnHealth(Entity entity, float requested, DamageSource? source)
    {
        var s = SessionFor(entity);
        if (!float.IsFinite(requested) || s.Deceased || !entity.Alive) return OwnHealth(entity);
        s.Framework = s.Framework with { Vitals = s.Framework.Vitals.WithNutrition(s.Framework.Nutrition, s.State, s.Profile) };
        var previous = s.Framework.Vitals;
        if (IsOwnedDowned(entity))
        {
            // Downed's floor and passed-out HP animation are projections, not a regional damage/heal setter.
            s.Framework = s.Framework with { Downed = new() { Active = true,
                ProjectedHealth = Math.Clamp(requested, NativeDownedBridge.Floor(entity), s.Framework.Vitals.EffectiveMaximum) } };
            return OwnHealth(entity);
        }
        var delta = requested - previous.Health;
        var resolution = delta < 0 ? NativePhysiologyBridge.CurrentRegion(entity, source) ?? ResolveHitRegion(entity, source) : new HitRegion("general", InjuryEvidence.Unlocated);
        s.LastHitPosition = source?.HitPosition is { } hit && double.IsFinite(hit.X) && double.IsFinite(hit.Y) && double.IsFinite(hit.Z)
            ? $"{hit.X:0.###},{hit.Y:0.###},{hit.Z:0.###}" : "";
        s.LastResolvedRegion = resolution.Region;
        s.LastHitQuality = resolution.Evidence.ToString();
        s.LastDamageAmount = Math.Max(0, -delta);
        var gravity = source?.Type == EnumDamageType.Gravity;
        var next = previous.Change(delta, resolution.VitalIndex, gravity);
        var frame = s.Framework with { Vitals = next };
        var classification = ClassifyWound(source);
        var actualDamage = Math.Max(0, Enumerable.Range(0, 6).Sum(i => previous.EffectiveFor(i) - next.EffectiveFor(i)));
        if (actualDamage > 0 && settings.Config.EnableWoundTracking && classification is { } classified)
        {
            var kind = classified.Kind;
            var injuryId = $"{s.LifeGeneration}:{frame.EventSequence + 1}";
            void Record(string region, double loss, double effectiveLoss, InjuryEvidence evidence)
            {
                if (effectiveLoss <= 0) return;
                // Game integrity loss, never inferred blood volume, bone fracture or a second HP charge.
                var wound = frameworkKernel.RecordInjury(frame, new(injuryId + ":" + region, kind, region,
                    Math.Clamp(effectiveLoss / (Anatomy.VitalIndex(region) is var index && index >= 0 ? previous.EffectiveMaximumFor(index) : previous.EffectiveMaximum), .001, 1), evidence, s.State.ElapsedBioSeconds,
                    classified.Bleeds ? WoundCare.InitialBleeding(effectiveLoss) : 0, IntegrityDamage: effectiveLoss));
                if (wound.Accepted) frame = wound.State;
            }
            if (resolution.VitalIndex < 0 && !gravity)
                Record("general", previous.Integrity.Sum() - next.Integrity.Sum(), actualDamage, InjuryEvidence.Unlocated);
            else for (var i = 0; i < 6; i++)
                Record(i == resolution.VitalIndex ? resolution.Region : VitalState.Regions[i],
                    previous.Integrity[i] - next.Integrity[i], previous.EffectiveFor(i) - next.EffectiveFor(i),
                    i == resolution.VitalIndex ? resolution.Evidence : InjuryEvidence.GameplayDistribution);
        }
        if (frame.Validate() is not null) return OwnHealth(entity);
        if (actualDamage > 0 && classification is not null && source?.Type != EnumDamageType.Suffocation
            && (source?.Type != EnumDamageType.Injury || source.Source is EnumDamageSource.Entity or EnumDamageSource.Player))
        {
            var bleeding = ClinicalBleeding(s, frame) > ClinicalBleeding(s) + 1e-10;
            var adrenaline = frameworkKernel.ActivateAdrenaline(frame, s.State, s.Profile,
                s.State.ElapsedBioSeconds, actualDamage, bleeding);
            if (adrenaline.Accepted)
            {
                frame = adrenaline.State;
                s.State = adrenaline.Body;
            }
        }
        if (actualDamage > 0 && settings.Config.EnableClinicalEffects
            && VerifiedBite(source) is { } bite)
            frame = DiseaseSyndromes.DirectExposure(frameworkKernel, frame, bite, settings.Config.DiseaseExposure.BiteHazard, "animal-bite");
        s.Framework = frame;
        if (next.Lethal && NativeDownedBridge.TryEnter(entity, source ?? new DamageSource {
            Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury })) SyncDowned(entity);
        return OwnHealth(entity);
    }

    internal static string? VerifiedBite(DamageSource? source)
    {
        // VS 1.22.7 canine melee animation. Bear claws, boar tusks, projectiles and
        // third-party generic animal damage are not evidence of an infectious bite.
        if (source?.Source is not EnumDamageSource.Entity
            || source.Type is not (EnumDamageType.SlashingAttack or EnumDamageType.PiercingAttack)
            || source.SourceEntity is not EntityAgent animal || animal is EntityPlayer
            || animal.Code is not { Domain: "game" } code
            || !code.Path.Split('-').Any(t => t is "wolf" or "hyena" or "fox")
            || animal.AnimManager?.ActiveAnimationsByAnimCode?.Keys.Any(k => k is "attack" or "attack-withwindup") != true)
            return null;
        return DiseaseExposure.AnimalBitePathogen(code.ToString());
    }

    // One mapping for the native health writer and observed damage cues.
    // Systemic loss changes health but cannot fabricate a skin wound or blood.
    internal static (WoundKind Kind, bool Bleeds)? ClassifyWound(DamageSource? source)
    {
        if (source is null) return null;
        return source.Type switch {
            EnumDamageType.Heal or EnumDamageType.Hunger => null,
            // Suffocation and other internal systemic losses are not physical wounds.
            EnumDamageType.Suffocation => null,
            EnumDamageType.SlashingAttack => (WoundKind.Cut, true),
            EnumDamageType.PiercingAttack => (WoundKind.Puncture, true),
            EnumDamageType.BluntAttack or EnumDamageType.Gravity or EnumDamageType.Crushing => (WoundKind.Contusion, false),
            EnumDamageType.Fire or EnumDamageType.Heat => (WoundKind.Burn, false),
            EnumDamageType.Frost => (WoundKind.Frostbite, false),
            EnumDamageType.Electricity => (WoundKind.ElectricBurn, false),
            EnumDamageType.Poison or EnumDamageType.Acid => (WoundKind.PoisonWound, false),
            EnumDamageType.Injury => source.SourceEntity is not null || source.CauseEntity is not null
                || source.Source is EnumDamageSource.Entity or EnumDamageSource.Player ? (WoundKind.Generic, true) : null,
            _ => null
        };
    }
    internal void CheckOwnDeath(Entity entity)
    {
        var s = SessionFor(entity);
        SyncExternalMaximum(s);
        if (s.Deceased || !entity.Alive || !s.Framework.Vitals.Lethal || IsOwnedDowned(entity)) return;
        var source = new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury };
        if (NativeDownedBridge.TryEnter(entity, source)) SyncDowned(entity);
        else entity.Die(EnumDespawnReason.Death, source);
    }
    internal static HitRegion ResolveHitRegion(Entity entity, DamageSource? source)
    {
        if (source?.Type == EnumDamageType.Gravity) return new("general", InjuryEvidence.GameplayDistribution);
        if (source is Seraph.Application.IVerifiedInjuryLocation verified && Anatomy.Known(verified.InjuryRegion)
            && verified.InjuryRegion != "general") return new(verified.InjuryRegion, InjuryEvidence.Verified);
        var combat = source is not null && (source.Type is EnumDamageType.SlashingAttack
            or EnumDamageType.PiercingAttack or EnumDamageType.BluntAttack or EnumDamageType.Crushing
            || source.Type == EnumDamageType.Injury && (source.SourceEntity is not null || source.CauseEntity is not null
                || source.Source is EnumDamageSource.Entity or EnumDamageSource.Player));
        var yaw = entity is EntityAgent agent ? agent.BodyYawServer : entity.Pos.Yaw;
        var box = entity.SelectionBox;
        var upright = entity is not EntityPlayer player || !(player.Swimming || player.MountedOn is not null
            || player.Controls.FloorSitting || player.WatchedAttributes.GetInt(Seraph.World.ClosedEyePose.StateAttribute) == 2);
        if (upright && source?.HitPosition is Vec3d hit && box is not null)
        {
            var located = Anatomy.Locate(hit.X, hit.Y - box.Y1, hit.Z, yaw, box.YSize,
                Math.Max(Math.Max(Math.Abs(box.X1), Math.Abs(box.X2)), Math.Max(Math.Abs(box.Z1), Math.Abs(box.Z2))));
            if (located.Evidence != InjuryEvidence.Unlocated) return located;
        }
        if (!combat) return new("general", InjuryEvidence.Unlocated);
        // Missing/pose-incompatible coordinates are estimates, never a diagnosis. One server draw
        // per accepted damage write; the chosen result is reused for integrity, wounds and diagnostics.
        var draw = entity.World?.Rand.NextDouble() ?? .5;
        var attacker = source!.SourceEntity ?? source.CauseEntity;
        var height = Math.Max(.5, box?.YSize ?? 1.8);
        var seated = entity is EntityPlayer ep && ep.Controls.FloorSitting;
        var horizontal = entity is EntityPlayer hp && (hp.Swimming || hp.WatchedAttributes.GetInt(Seraph.World.ClosedEyePose.StateAttribute) == 2);
        var strikeHeight = .65;
        double lateral = 0;
        if (attacker is not null)
        {
            var dx = attacker.Pos.X - entity.Pos.X; var dz = attacker.Pos.Z - entity.Pos.Z;
            lateral = dx * Math.Cos(yaw) - dz * Math.Sin(yaw);
            var eye = attacker is EntityAgent aa ? aa.LocalEyePos.Y : 0;
            var aimedY = attacker.Pos.Y + eye;
            if (attacker is IProjectile && Math.Abs(attacker.Pos.Motion.Y) > .0001)
            {
                var speed = Math.Sqrt(attacker.Pos.Motion.X * attacker.Pos.Motion.X + attacker.Pos.Motion.Z * attacker.Pos.Motion.Z);
                if (speed > .001) aimedY += attacker.Pos.Motion.Y * Math.Min(3, Math.Sqrt(dx * dx + dz * dz) / speed);
            }
            strikeHeight = (aimedY - entity.Pos.Y) / (height * (seated ? .65 : 1));
        }
        var side = Math.Abs(lateral) < .05 ? (draw < .5 ? "left-" : "right-") : lateral < 0 ? "left-" : "right-";
        var h = Math.Clamp(strikeHeight * .7 + .15 + (draw - .5) * .7, 0, 1);
        var region = horizontal
            ? draw < .12 ? "head-neck" : draw < .45 ? "chest" : draw < .65 ? "abdomen" : side + (draw < .82 ? "arm" : "leg")
            : h >= .88 ? "head-neck" : h < .1 ? side + "foot" : h < .43 ? side + "leg"
            : draw < .28 || draw > .86 ? side + (h < .60 ? "hand" : "arm") : h >= .62 ? "chest" : "abdomen";
        return new(region, InjuryEvidence.GameplayDistribution);
    }
    internal void ProjectPhysiology(Entity entity)
    {
        if (!OwnsPhysiology(entity)) return;
        var s = SessionFor(entity);
        s.Framework = s.Framework with { Vitals = s.Framework.Vitals.WithNutrition(s.Framework.Nutrition, s.State, s.Profile) };
        var tree = entity.WatchedAttributes.GetTreeAttribute("health");
        if (tree is not null)
        {
            var value = OwnHealth(entity);
            if (tree.GetFloat("currenthealth") != value || tree.GetFloat("maxhealth") != OwnMaxHealth(entity))
            {
                tree.SetFloat("currenthealth", value); tree.SetFloat("maxhealth", OwnMaxHealth(entity));
                entity.WatchedAttributes.MarkPathDirty("health");
            }
        }
        CheckOwnDeath(entity);
        var hunger = entity.WatchedAttributes.GetTreeAttribute("hunger");
        if (hunger is null) return;
        // Compatibility fullness only. The five native food groups are never simulated or read.
        var fullness = Math.Clamp(Math.Max(s.Framework.Nutrition.ContentsMassG(s.State.GutWaterL) / DietaryMetabolism.StomachCapacityG, s.State.GutEnergyKJ / s.Profile.MaximumGutEnergyKJ), 0, 1);
        var saturation = (float)(fullness * 1500);
        var changed = hunger.GetFloat("currentsaturation") != saturation || hunger.GetFloat("maxsaturation") != 1500;
        if (changed) { hunger.SetFloat("currentsaturation", saturation); hunger.SetFloat("maxsaturation", 1500); }
        foreach (var key in NativeFoodGroups)
            if (hunger.GetFloat(key) != 0) { hunger.SetFloat(key, 0); changed = true; }
        if (changed) entity.WatchedAttributes.MarkPathDirty("hunger");
    }
    private static readonly string[] NativeFoodGroups = ["fruitLevel", "vegetableLevel", "proteinLevel", "grainLevel", "dairyLevel"];
}
