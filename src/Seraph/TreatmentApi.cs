using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Seraph;

public sealed record TreatmentUseResult(bool Success, string Message, int RecentDoses = 0, long Wound = 0);

public sealed partial class SeraphModSystem
{
    /// <summary>Read the main mod's verified water metadata without duplicating its storage format.</summary>
    public Seraph.Core.Framework.WaterBatch? ReadHerbalWater(ItemStack liquid)
        => coreApi is { } api ? World.WaterQuality.ReadLiquidAt(api.World, liquid, 15, InteractionConfig)
            : World.WaterQuality.ReadLiquid(liquid, 15, InteractionConfig);

    private readonly Dictionary<string, TreatmentDefinition> treatmentDefinitions = new(StringComparer.Ordinal);

    /// <summary>Register once at startup. One physical item is one fixed dose; registration never accepts a client payload.</summary>
    public void RegisterTreatment(string itemCode, TreatmentDefinition definition)
        => throw new NotSupportedException("Use RegisterTreatmentV1 with normalized game seconds.");

    public void RegisterTreatmentV1(string itemCode, TreatmentDefinition definition)
    {
        if (new AssetLocation(itemCode).ToString() != itemCode || definition.Validate() is { })
            throw new ArgumentException("Invalid treatment registration.");
        var snapshot = definition with { Pathogens = [.. definition.Pathogens], Symptoms = [.. definition.Symptoms], WoundKinds = [.. definition.WoundKinds] };
        if (!treatmentDefinitions.TryAdd(itemCode, snapshot)) throw new ArgumentException("Duplicate treatment item: " + itemCode);
    }

    /// <summary>Check a held dose without starting or cancelling an action, consuming items or changing physiology.</summary>
    public TreatmentUseResult QueryTreatment(IServerPlayer player, ItemSlot slot, TreatmentRoute route)
        => slot.Itemstack is { } stack && treatmentDefinitions.TryGetValue(stack.Collectible.Code.ToString(), out var def)
            ? server?.QueryTreatment(player, slot, def, route) ?? new(false, "treatment-unavailable")
            : new(false, "treatment-unknown");

    public TreatmentUseResult BeginTreatment(IServerPlayer player, ItemSlot slot, TreatmentRoute route)
        => slot.Itemstack is { } stack && treatmentDefinitions.TryGetValue(stack.Collectible.Code.ToString(), out var def)
            ? server?.BeginTreatment(player, slot, def, route) ?? new(false, "treatment-unavailable")
            : new(false, "treatment-unknown");
    public bool ContinueTreatment(IServerPlayer player, ItemSlot slot) => server?.ContinueTreatment(player, slot) == true;
    public TreatmentUseResult CompleteTreatment(IServerPlayer player, ItemSlot slot) => server?.CompleteTreatment(player, slot)
        ?? new(false, "treatment-unavailable");
    public void CancelTreatment(IServerPlayer player) => server?.CancelTreatment(player);

    /// <summary>Only explicit server-verified contact, never a periodic random diagnosis.</summary>
    public void RecordPlantContact(IServerPlayer player, string source, double severity) => server?.RecordPlantContact(player, source, severity);
}
