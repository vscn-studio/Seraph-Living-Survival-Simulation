using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private static PanelMetric[] DebugValues(Session s)
    {
        PanelMetric Value(string key, double value, double maximum = 100, double minimum = 0, string unit = "%") => new()
        { Key = key, Value = value, Maximum = maximum, Minimum = minimum, Available = true, Unit = unit };
        return [Value("bowel", s.Interactions.BowelUnits, BowelSettlement.CapacityG, unit: "g"),
            Value("pending-bowel", s.Interactions.PendingBowelUnits, BowelSettlement.MaximumPendingG, unit: "g"),
            Value("stamina", s.Framework.Stamina * 100), Value("fatigue", s.Framework.Fatigue * 100),
            Value("sleep-pressure", s.State.SleepPressure * 100), Value("pain", s.Framework.Pain * 100),
            Value("dirtiness", s.Framework.Dirtiness * 100), Value("immune-reserve", s.Framework.ImmuneReserve * 100),
            Value("electrolytes", s.Framework.Nutrition.Reserve[4], unit: "g"),
            Value("core-temperature", s.State.CoreTemperatureC, 43, 27, "°C"),
            Value("skin-temperature", s.State.SkinTemperatureC, 45, 0, "°C"),
            Value("carbon-monoxide", s.Framework.CarbonMonoxideBurden * 100),
            Value("toxic-burden", s.Framework.ToxicBurden * 100)];
    }

    private void OnDebugValue(IServerPlayer player, DebugValueRequest packet)
    {
        if (!Current(player, out var session)) return;
        var response = HandleDebugValue(session, packet);
        if (response is null) return;
        channel.SendPacket(response, player);
        SendSnapshot(session);
    }

    private DebugValueResponse? HandleDebugValue(Session session, DebugValueRequest packet)
    {
        if (!session.HelloAccepted || packet.ProtocolVersion != Protocol.Version || packet.SessionEpoch != session.Epoch
            || packet.RequestId <= 0) return null;
        if (session.LastDebugResponse is { } previous && packet.RequestId <= previous.RequestId)
            return packet.RequestId == previous.RequestId ? previous : null;
        var error = ApplyDebugValue(session, packet);
        return session.LastDebugResponse = new()
        {
            RequestId = packet.RequestId, SessionEpoch = session.Epoch, Accepted = error is null,
            Message = L(session.Player, error ?? "debug-edit-applied")
        };
    }

    private string? ApplyDebugValue(Session session, DebugValueRequest packet)
    {
        if (!session.Player.HasPrivilege(Privilege.controlserver) || !session.WantsDiagnostics) return "debug-edit-denied";
        if (startupFault is not null || session.Fault is not null || session.Deceased || session.PendingRespawn
            || !session.Player.Entity.Alive || packet.LifeGeneration != session.LifeGeneration) return "debug-edit-unavailable";
        if (session.Wash is not null || session.Defecation is not null || session.LatrineDefecation is not null
            || session.GroundDrink is not null || session.WellBucketRaise is not null || session.Pressure is not null)
            return "debug-edit-busy";
        var definition = DebugValues(session).FirstOrDefault(v => v.Key == packet.Field);
        if (definition is null || !double.IsFinite(packet.Value) || packet.Value < definition.Minimum || packet.Value > definition.Maximum)
            return "debug-edit-invalid";
        var state = session.State;
        var framework = session.Framework;
        var interactions = session.Interactions;
        var v = packet.Value;
        switch (packet.Field)
        {
            case "bowel": session.Interactions = interactions with { BowelUnits = v }; break;
            case "pending-bowel": session.Interactions = interactions with { PendingBowelUnits = v }; break;
            case "stamina": session.Framework = framework with { Stamina = v / 100 }; break;
            case "fatigue": session.Framework = framework with { Fatigue = v / 100 }; break;
            case "sleep-pressure": session.State = state with { SleepPressure = v / 100 }; break;
            case "pain": session.Framework = framework with { Pain = v / 100 }; break;
            case "dirtiness": session.Framework = framework with { Dirtiness = v / 100 }; break;
            case "immune-reserve": session.Framework = framework with { ImmuneReserve = v / 100 }; break;
            case "electrolytes":
                var salt = (double[])framework.Nutrition.Reserve.Clone(); salt[4] = v;
                session.Framework = framework with { Nutrition = framework.Nutrition with { Reserve = salt } };
                session.State = state with { ElectrolyteBalance = v / 100 }; break;
            case "core-temperature": session.State = state with { CoreTemperatureC = v }; break;
            case "skin-temperature": session.State = state with { SkinTemperatureC = v }; break;
            case "carbon-monoxide": session.Framework = framework with { CarbonMonoxideBurden = v / 100 }; break;
            case "toxic-burden": session.Framework = framework with { ToxicBurden = v / 100 }; break;
        }
        if (!Save(session))
        {
            session.State = state; session.Framework = framework; session.Interactions = interactions;
            return "debug-edit-save-failed";
        }
        api.Logger.Notification("[vsslssmod] Admin {0} edited own {1}: {2} -> {3} {4}.", session.Player.PlayerUID,
            packet.Field, definition.Value, packet.Value, definition.Unit);
        return null;
    }
}
