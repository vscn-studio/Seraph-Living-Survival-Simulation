using Vintagestory.API.Common;

namespace Seraph.World;

internal static class VomitPose
{
    internal const string Code = "vsslssmod-vomit";

    internal static AnimationMetaData Create() => new AnimationMetaData
    {
        Code = Code, Animation = Code, Weight = 100,
        BlendMode = EnumAnimationBlendMode.Average,
        SupressDefaultAnimation = true, EaseInSpeed = 8, EaseOutSpeed = 8,
        ElementWeight = new() { ["LowerTorso"] = 100, ["UpperTorso"] = 100, ["Head"] = 100 }
    }.Init();
}
