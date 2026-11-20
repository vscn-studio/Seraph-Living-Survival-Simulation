using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Seraph.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

internal static class AdrenalineAudioChecks
{
    internal static void Run()
    {
        foreach (var path in new[] { "sounds/creature/wolf/attack1.ogg", "sounds/walk/stone1.ogg", "sounds/player/hurt1.ogg", "sounds/player/hackingspearhit.ogg" })
            if (!AdrenalineAudio.DangerSound(new(new AssetLocation("game", path)))) throw new Exception("Missing alert sound: " + path);
        foreach (var path in new[] { "sounds/weather/rain.ogg", "sounds/environment/water.ogg", "sounds/gui/button.ogg", "sounds/body/heartbeat.ogg", "music/night.ogg" })
            if (AdrenalineAudio.DangerSound(new(new AssetLocation("game", path)))) throw new Exception("Unwanted alert sound: " + path);
        var kind = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.LoadedSoundNative", true)!;
        var getter = AccessTools.PropertyGetter(kind, "GlobalVolume");
        var native = RuntimeHelpers.GetUninitializedObject(kind);
        GC.SuppressFinalize(native); // No audio device/source was constructed.
        var sound = new SoundParams(new AssetLocation("game:sounds/creature/wolf/attack1.ogg")) { Volume = .3f };
        AccessTools.Field(kind, "soundParams").SetValue(native, sound);
        var alert = AccessTools.Field(typeof(AdrenalineAudio), "alert");
        var patch = new Harmony("vsslssmod.tests.soundgain");
        try
        {
            alert.SetValue(null, 0f);
            var baseline = (float)getter.Invoke(native, null)!;
            patch.Patch(getter, postfix: new HarmonyMethod(AccessTools.Method(typeof(AdrenalineAudio), "Gain")));
            alert.SetValue(null, 1f);
            var louder = (float)getter.Invoke(native, null)!;
            if (Math.Abs(louder - baseline * 1.25) > 1e-6 || sound.Volume != .3f) throw new Exception("Sound gain changed stored volume or missed native getter.");
            sound.SoundType = EnumSoundType.Music;
            var music = (float)getter.Invoke(native, null)!;
            alert.SetValue(null, 0f);
            if ((float)getter.Invoke(native, null)! != music) throw new Exception("Music was amplified.");
            sound.SoundType = EnumSoundType.Sound;
            if (Math.Abs((float)getter.Invoke(native, null)! - baseline) > 1e-6) throw new Exception("Sound volume was not restored.");
        }
        finally { alert.SetValue(null, 0f); patch.UnpatchAll("vsslssmod.tests.soundgain"); }
        Console.WriteLine("PASS native sound gain hook: danger classification, +25%, music exclusion, expiry and stored volume preservation");
    }
}
