using Vintagestory.API.Common;

namespace Seraph.World;

internal static class DefecationPose
{
    internal const string Code = "vsslssmod-squat";

    // A dedicated, untriggered pose: vanilla Sneak/Idle control updates must not stop it.
    internal static AnimationMetaData Create() => new AnimationMetaData
    {
        Code = Code, Animation = Code, Weight = 100,
        BlendMode = EnumAnimationBlendMode.Average,
        SupressDefaultAnimation = true, EaseInSpeed = 8, EaseOutSpeed = 8,
        ElementWeight = new()
        {
            ["LowerTorso"] = 100, ["UpperTorso"] = 100,
            ["UpperFootL"] = 100, ["LowerFootL"] = 100,
            ["UpperFootR"] = 100, ["LowerFootR"] = 100
        }
    }.Init();
}
