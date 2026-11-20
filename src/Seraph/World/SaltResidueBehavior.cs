using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Seraph.Core.Framework;

namespace Seraph.World;

public sealed class SaltResidueBehavior(BlockEntity entity) : BlockEntityBehavior(entity)
{
    internal const string Key = "vsslssmod:salt-residue-g";
    public double Grams { get; set; }
    public override void ToTreeAttributes(ITreeAttribute tree) { base.ToTreeAttributes(tree); tree.SetDouble(Key, Grams); }
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
    { base.FromTreeAttributes(tree, world); Grams = Safe(tree.GetDouble(Key)); }
    public override void OnBlockPlaced(ItemStack? stack = null)
    { base.OnBlockPlaced(stack); Grams = Safe(stack?.Attributes.GetDouble(Key) ?? 0); }
    public override void GetBlockInfo(IPlayer player, StringBuilder text)
    {
        base.GetBlockInfo(player, text);
        if (Grams > 0) text.AppendLine(LocalizedText.Get("vsslssmod:salt-residue", Grams.ToString("0.##"), (int)(Grams / WaterDistillation.SaltGramsPerItem)));
    }
    private static double Safe(double grams) => double.IsFinite(grams) ? Math.Clamp(grams, 0, 4000000) : 0;
}
