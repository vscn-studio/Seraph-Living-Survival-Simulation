using Seraph.Core.Framework;
using Vintagestory.API.Common;

namespace Seraph.World;

internal enum TreatmentKind { Empty, Filter, Drain, Pipe }

internal sealed record FilterMaterial(string Code, EnumItemClass Class, int Quantity)
{
    internal ItemStack? Resolve(IWorldAccessor world)
    {
        var code = new AssetLocation(Code);
        CollectibleObject? collectible = Class == EnumItemClass.Block ? world.GetBlock(code) : world.GetItem(code);
        return collectible is null ? null : new ItemStack(collectible, Quantity);
    }
}

internal sealed class TreatmentSlot
{
    public TreatmentKind Kind { get; set; }
    public int Direction { get; set; }
    public string OwnerUid { get; set; } = "";
    public WaterBatch? Water { get; set; }
    public FilterMaterial? Gravel { get; set; }
    public FilterMaterial? Charcoal { get; set; }
    public FilterMaterial? Cloth { get; set; }
    public double UsedL { get; set; }
    public bool Flowing { get; set; }
    internal double Volume => Water?.VolumeL ?? 0;
    internal double FlowLPerSecond => Kind == TreatmentKind.Filter ? WaterFiltration.FlowLPerSecond
        : Cloth is not null ? WaterFiltration.ClothFlowLPerSecond : WaterFiltration.DrainFlowLPerSecond;
    internal bool Complete => Kind == TreatmentKind.Drain || Gravel is not null && Charcoal is not null && Cloth is not null;
    internal double RemainingL => Math.Max(0, WaterFiltration.MediaLifeL - UsedL);
    internal bool Ready => Complete && (Kind == TreatmentKind.Drain && Cloth is null || RemainingL > .0001);
    internal string? NextMaterial => Kind == TreatmentKind.Drain ? Cloth is null ? "linen" : null
        : Gravel is null ? "gravel" : Charcoal is null ? "charcoal" : Cloth is null ? "linen" : null;

    internal bool Valid()
    {
        if (!Enum.IsDefined(Kind) || Direction is < 0 or > 3 || OwnerUid is null || OwnerUid.Length > 128
            || !double.IsFinite(UsedL) || UsedL is < 0 or > WaterFiltration.MediaLifeL
            || Water is not null && (Water.Validate() is not null || Volume > WaterFiltration.ReservoirL)) return false;
        if (Kind is TreatmentKind.Empty or TreatmentKind.Pipe)
            return Water is null && Gravel is null && Charcoal is null && Cloth is null;
        if (Gravel is not null && !Matches(Gravel, "gravel") || Charcoal is not null && !Matches(Charcoal, "charcoal")
            || Cloth is not null && !Matches(Cloth, "linen")) return false;
        if (Kind == TreatmentKind.Filter && (Charcoal is not null && Gravel is null || Cloth is not null && Charcoal is null)) return false;
        return Kind != TreatmentKind.Drain || Gravel is null && Charcoal is null;
    }

    internal static bool Matches(FilterMaterial material, string layer)
    {
        if (string.IsNullOrWhiteSpace(material.Code)) return false;
        var parts = material.Code.Split(':');
        if (parts.Length != 2 || parts[0] != "game") return false;
        var path = parts[1];
        return layer switch
        {
            "gravel" => material.Class == EnumItemClass.Block && path.StartsWith("gravel-", StringComparison.Ordinal) && material.Quantity == 1
                || material.Class == EnumItemClass.Item && path.StartsWith("stone-", StringComparison.Ordinal) && material.Quantity == 4,
            "charcoal" => material.Class == EnumItemClass.Item && path == "charcoal" && material.Quantity == 2,
            "linen" => material.Class == EnumItemClass.Block && path.StartsWith("linen-", StringComparison.Ordinal) && material.Quantity == 1,
            _ => false
        };
    }

    internal bool TryInsert(ItemStack stack, out int quantity)
    {
        quantity = 0;
        if (NextMaterial is not { } layer) return false;
        quantity = layer == "charcoal" ? 2 : layer == "gravel" && stack.Class == EnumItemClass.Item ? 4 : 1;
        var material = new FilterMaterial(stack.Collectible.Code.ToString(), stack.Class, quantity);
        if (stack.StackSize < quantity || !Matches(material, layer)) { quantity = 0; return false; }
        switch (layer) { case "gravel": Gravel = material; break; case "charcoal": Charcoal = material; break; default: Cloth = material; break; }
        return true;
    }

    internal WaterBatch Product(double litres) => WaterFiltration.Process(Water! with { VolumeL = litres }, Kind == TreatmentKind.Filter, Cloth is not null);
    internal void Consume(double litres)
    {
        if (Cloth is not null) UsedL = Math.Min(WaterFiltration.MediaLifeL, UsedL + litres);
        Water = Volume - litres < .00001 ? null : Water! with { VolumeL = Volume - litres };
    }
    internal double Accept(WaterBatch batch)
    {
        if (batch.Validate() is not null || Kind is not (TreatmentKind.Filter or TreatmentKind.Drain)) return 0;
        var litres = Math.Min(batch.VolumeL, WaterFiltration.ReservoirL - Volume);
        if (litres <= 0) return 0;
        var incoming = batch with { VolumeL = litres };
        Water = Water is null ? incoming : Water.Mix(incoming);
        return litres;
    }
}
