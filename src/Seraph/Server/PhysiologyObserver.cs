using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seraph.Server;

/// <summary>Additive passthrough. Does not replace vanilla behaviors or change damage/saturation.</summary>
internal sealed class PhysiologyObserver(Entity entity, Action<float, EnumFoodCategory> saturation,
    Action<EnumDamageType> damage) : EntityBehavior(entity)
{
    public override string PropertyName() => "vsslssmod-observer";
    public override void OnEntityReceiveSaturation(float value, EnumFoodCategory foodCat = EnumFoodCategory.Unknown,
        float saturationLossDelay = 10, float nutritionGainMultiplier = 1)
    {
        if (float.IsFinite(value) && value > 0 && value <= 100000) saturation(value, foodCat);
    }
    public override void OnEntityReceiveDamage(DamageSource source, ref float value)
    {
        if (float.IsFinite(value) && value > 0 && source is not null) damage(source.Type);
    }
}
