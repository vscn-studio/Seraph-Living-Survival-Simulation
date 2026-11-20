namespace Seraph.Application;

public enum CapabilityOwner { Native, Vsslssmod, External, ObserveOnly }
public enum Capability { ThermalDamage, Hydration, Nutrition, Health, Movement, Sleep, OxygenDamage, BleedingDamage, Downed, DeathInventory, Alcohol, Infection, Manipulation, SmokeExposure }
public sealed record CapabilityAssignment(Capability Capability, CapabilityOwner Owner, string Provider, string TimeBasis, string Reason);

/// <summary>Fixed at startup. Registration never removes or renames another mod's behaviors.</summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<Capability, CapabilityAssignment> owners = [];
    public IReadOnlyCollection<CapabilityAssignment> Assignments => owners.Values;
    public void Register(CapabilityAssignment assignment)
    {
        if (!owners.TryAdd(assignment.Capability, assignment)) throw new InvalidOperationException("Capability already has an owner.");
    }
    public CapabilityAssignment this[Capability capability] => owners[capability];
    public bool MayWrite(Capability capability) => owners.TryGetValue(capability, out var owner) && owner.Owner == CapabilityOwner.Vsslssmod;

    public static CapabilityRegistry Create(SeraphServerConfig config, IEnumerable<(string Id, string Name)> loaded)
    {
        var mods = loaded.ToArray();
        string? Find(params string[] ids) => mods.FirstOrDefault(m => ids.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).Id;
        var result = new CapabilityRegistry();
        var active = config.Mode == "survival";
        void Add(Capability capability, string? external = null, bool own = false, string reason = "native effects retained") => result.Register(
            new(capability, external is not null ? CapabilityOwner.External : own ? active ? CapabilityOwner.Vsslssmod : CapabilityOwner.ObserveOnly : CapabilityOwner.Native,
                external ?? (own && active ? "vsslssmod" : "game"), own && active ? "bio-seconds" : "provider-owned", reason));
        Add(Capability.ThermalDamage, own: config.EnableClinicalEffects, reason: "core-temperature consequences; only native Weather/Frost damage delegated to this owner");
        Add(Capability.Hydration, own: true, reason: "server water ledger, dehydration functions and severe deficit damage");
        Add(Capability.Nutrition, own: true, reason: "verified item/serving composition, independent reserves; original five groups and hunger costs disabled");
        Add(Capability.Health, own: true, reason: "regional integrity and lethal thresholds own health; native armor/DoT/death events and alcohol side effects retained");
        Add(Capability.Movement, own: config.EnableMovementEffects,
            reason: "one bounded nonpersistent physiological source; mounted movement excluded; other stat sources retained");
        Add(Capability.Manipulation, Find("combatoverhaul", "combat overhaul", "combatrealism"), config.EnableInjuryActionEffects,
            "injury capability at native mining/melee/bow execution; conservative delegation to combat overhauls");
        Add(Capability.Sleep, Find("comfybeds", "comfybed"), reason: "native bed mounting and multiplayer eligibility retained; Seraph calendar adapter coordinates pressure recovery and sleep acceleration");
        Add(Capability.SmokeExposure, Find("realsmoke"), reason: "external smoke simulation retained; verified Real Smoke relative concentration may feed SLSS acute real-time dose; external asphyxia retained");
        Add(Capability.OxygenDamage, Find("realsmoke"), reason: "native/external suffocation only; oxygen ratio is not SpO2");
        Add(Capability.BleedingDamage, own: config.EnableClinicalEffects, reason: "player wound-volume ledger with native bandages; BloodTrail particles are optional; external player loss hooks are suppressed only while Seraph owns bleeding, animals retain external behavior");
        Add(Capability.Downed, Find("downed"), reason: "no native behavior replacement or competing revival");
        Add(Capability.DeathInventory, Find("dead"), reason: "inventory and corpse owner retained");
        Add(Capability.Alcohol, Find("slowtox"), reason: "no parallel intoxication writer");
        Add(Capability.Infection, own: config.EnableClinicalEffects, reason: "verified water and contaminated open wounds supply exposure; incubation, symptoms and recovery");
        return result;
    }
}

public enum SideEffectKind { PrivateState, HitPoints, Stats, NativeAttributes, Inventory, Medical, Revival, Action }
public sealed class SideEffectBarrier(string mode, CapabilityRegistry capabilities)
{
    public bool Allows(SideEffectKind kind, Capability? capability = null) => kind == SideEffectKind.PrivateState
        || mode == "survival" && capability.HasValue && capabilities.MayWrite(capability.Value)
        && (kind == SideEffectKind.Stats || kind == SideEffectKind.Action && capability is Capability.Movement or Capability.Manipulation);
}

public sealed record IntegrationStatus(string Id, string Version, string Name, string Status, string Policy);
