using Vintagestory.API.Server;
using Seraph.Core.Framework;
using Seraph.Network;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private static NutrientGauge[] NutrientProjection(Session s) => Nutrients.All.Select((n, i) => new NutrientGauge {
        Key = n.Id, Value = s.Framework.Nutrition.Reserve[i], Maximum = n.Maximum,
        Pending = s.Framework.Nutrition.Pending[i], Available = s.Framework.Nutrition.Known[i],
        Deficiency = s.Framework.Nutrition.DeficiencyBurden[i],
        Stage = (int)s.Framework.Nutrition.Get(n.Id).Stage
    }).ToArray();
    private string PanelNumber(IServerPlayer player, double? value, string format) =>
        value is { } number && double.IsFinite(number) ? Number(number, format) : string.Empty;

    private string Indicator(Session s, string key, string unit, double? value = null, string format = "0.0") =>
        L(s.Player, "character-indicator", L(s.Player, key), unit, PanelNumber(s.Player, value, format));

    private string[] PanelNutritionLines(Session s)
    {
        return [
            L(s.Player, "nutrition-energy-bars", Number((s.State.GlycogenEnergyKJ + s.State.FatEnergyKJ) / 4.184, "0"),
                Number((s.Profile.GlycogenCapacityKJ + s.Profile.FatCapacityKJ) / 4.184, "0")),
            L(s.Player, "nutrition-glycogen-bars", Number(s.State.GlycogenEnergyKJ / 4.184, "0"), Number(s.Profile.GlycogenCapacityKJ / 4.184, "0")),
            L(s.Player, "nutrition-fat-bars", Number(s.State.FatEnergyKJ / 4.184, "0"), Number(s.Profile.FatCapacityKJ / 4.184, "0")),
            L(s.Player, "nutrition-gut-energy-bars", Number(s.State.GutEnergyKJ / 4.184, "0"), Number(s.Profile.MaximumGutEnergyKJ / 4.184, "0")),
            L(s.Player, "nutrition-current-gut", Number(s.Framework.Nutrition.ContentsMassG(s.State.GutWaterL), "0")),
            L(s.Player, "nutrition-profile-note")
        ];
    }

    private string[] PanelHydrationLines(Session s) =>
    [
        L(s.Player, "character-panel-usable-water", Number(ReserveDisplay.HydrationG(s.State, s.Profile), "0")),
        L(s.Player, "character-panel-blood-model", Number(s.State.BloodVolumeL(s.Profile), "0.00"))
    ];

    private string[] PanelWoundLines(Session s)
    {
        var lines = new List<string> { L(s.Player, "character-panel-wounds", s.Framework.Wounds.Length) };
        var now = s.State.ElapsedBioSeconds;
        foreach (var wound in s.Framework.Wounds.OrderByDescending(w => w.EventSequence))
        {
            lines.Add(L(s.Player, "character-wound-entry", wound.EventSequence,
                L(s.Player, "character-wound-" + wound.Kind.ToString().ToLowerInvariant()), Number(wound.Severity, "P0"), Number(wound.Healing, "P0")));
            lines.Add(L(s.Player, "character-wound-location", L(s.Player, "body-region-" + (Anatomy.Known(wound.Region) ? wound.Region : "general")),
                L(s.Player, "injury-evidence-" + wound.Evidence.ToString().ToLowerInvariant()),
                Number(BodyFunctionResolver.Remaining(wound), "P0")));
            lines.Add(L(s.Player, "character-wound-care", Number(Math.Max(0, wound.PressureUntilBioSeconds - now), "0"),
                Number(Math.Max(0, wound.CleanUntilBioSeconds - now) / 60, "0")));
        }
        if (s.Framework.Wounds.Length == 0) lines.Add(L(s.Player, "character-wound-none"));
        var action = medical.Get(s.Player.PlayerUID);
        if (action is not null) lines.Add(L(s.Player, "character-care-active",
            L(s.Player, "character-action-" + action.Action), Number(Math.Max(0, action.DurationSeconds - (ServerSeconds - action.StartedRealSeconds)), "0.0")));
        lines.Add(L(s.Player, "character-wound-source"));
        return lines.ToArray();
    }
}
