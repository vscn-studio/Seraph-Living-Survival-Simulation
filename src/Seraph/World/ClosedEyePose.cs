using Vintagestory.API.Common;

namespace Seraph.World;

internal static class ClosedEyePose
{
    internal const string Code = "vsslssmod-closed-eye-rest";
    internal const string StateAttribute = "vsslssmod-closed-eye-rest";

    internal static AnimationMetaData Create(bool reclined) => new AnimationMetaData
    {
        Code = Code,
        // These are shape animation names. "sleep" is only vanilla's metadata code.
        Animation = reclined ? "lie" : "sitflooridle",
        ClientSide = true,
        Weight = 100,
        BlendMode = EnumAnimationBlendMode.Average,
        SupressDefaultAnimation = true,
        EaseInSpeed = 8,
        EaseOutSpeed = 8,
        ElementWeight = new()
        {
            ["LowerTorso"] = 100, ["UpperTorso"] = 100,
            ["UpperArmL"] = 100, ["LowerArmL"] = 100,
            ["UpperArmR"] = 100, ["LowerArmR"] = 100,
            ["UpperFootL"] = 100, ["LowerFootL"] = 100,
            ["UpperFootR"] = 100, ["LowerFootR"] = 100
        }
    }.Init();
}
