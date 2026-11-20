using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed record ActivityObservation(ExertionKind Kind, bool Active, string Provider, string Evidence, double PowerW);
internal sealed record ActivityBridgeStatus(string Id, string Version, string Status, string Evidence);

/// <summary>Shared observation of native accepted work; speed, cargo and passenger input never imply work.</summary>
internal static class ActivityCompatibility
{
    internal static IMountableSeat? ControllerSeat(EntityAgent player)
    {
        var seat = player.MountedOn;
        return seat is { CanControl: true, Config.Controllable: true }
            && ReferenceEquals(seat.Passenger, player) && ReferenceEquals(seat.MountSupplier.Controller, player) ? seat : null;
    }

    // Raw controller input is distinct from a persistent cart gait: releasing the keys must permit recovery/unlock.
    internal static bool TriggerHeld(EntityAgent player)
        => ControllerSeat(player) is { } seat && seat.Controls is { } c
            && (c.TriesToMove || c.Jump || c.Sprint || NativeVehicleBridge.JauntDescending(seat.Entity, c));

    internal static ActivityObservation Observe(EntityAgent player, FunctionSnapshot? functions = null)
    {
        try
        {
            if (player.MountedOn is not null)
            {
                var seat = ControllerSeat(player);
                if (seat is null) return Idle("game", "passenger-or-uncontrolled-seat");
                var mount = seat.Entity;
                var c = seat.Controls;
                var f = functions ?? NativeInjuryActionBridge.Functions(player);
                if (f is { Manipulation: true, CanUseActiveHand: false }) return Idle("game", "human-control-denied");
                if (NativeVehicleBridge.IsShip(mount))
                {
                    if (!NativeVehicleBridge.ShipState(mount, player, out var sails)) return Idle("shipwright", "controller-or-paddle-unavailable");
                    var turn = c.Left ^ c.Right;
                    var thrust = !sails && (c.Forward || c.Backward);
                    var kind = sails ? ExertionKind.MountedControl : ExertionKind.Rowing;
                    return new(kind, turn || thrust, "shipwright", sails ? "manual-sail-steering" : "manual-paddle-control",
                        turn || thrust ? sails ? 180 : 300 : 0);
                }
                var accepted = NativeVehicleBridge.Accepted(seat);
                if (NativeVehicleBridge.IsCart(mount))
                {
                    var gait = mount.GetBehavior<EntityBehaviorGait>();
                    var pushing = accepted.Move && gait is { IsIdle: false, CurrentGait: not null }
                        && gait.CurrentGait.Direction != 0;
                    var turn = accepted.Move && accepted.Turn && (c.Left ^ c.Right);
                    var kind = pushing && gait is not null && NativeVehicleBridge.SprintGait(gait)
                        ? ExertionKind.Sprinting : ExertionKind.Carrying;
                    return new(kind, pushing || turn, "cartwrightscaravan", pushing ? "accepted-human-gait" : "manual-cart-turn",
                        pushing || turn ? Exertion.PowerW(kind) : 0);
                }
                if (seat.MountSupplier is EntityBehaviorRideable)
                {
                    var active = accepted.Move && (c.Forward || c.Backward || c.Jump
                        || NativeVehicleBridge.JauntDescending(mount, c) || accepted.Turn && (c.Left ^ c.Right));
                    return new(ExertionKind.MountedControl, active, "native-rideable", "native-permissions-and-controller", active ? 180 : 0);
                }
                return Idle("unknown-mount", "mount-provider-unverified");
            }
            var controls = player.World.Side == EnumAppSide.Client ? player.Controls : player.ServerControls;
            if (controls.IsClimbing) return new(ExertionKind.Climbing, controls.TriesToMove, "game", "native-climbing-control", 250);
            if (player.Swimming) return new(ExertionKind.Swimming, controls.TriesToMove, "game", "native-swimming-control", 180);
            return Idle("game", "no-special-activity");
        }
        catch (Exception ex)
        {
            NativeVehicleBridge.Warn("activity-observation", ex);
            return Idle("unknown-mount", "adapter-unavailable");
        }
    }

    private static ActivityObservation Idle(string provider, string evidence) => new(ExertionKind.Standing, false, provider, evidence, 0);

    internal static ActivityBridgeStatus[] Status(IEnumerable<(string Id, string Version)> loaded)
    {
        var list = new List<ActivityBridgeStatus>();
        foreach (var (id, version) in loaded)
        {
            var (status, evidence) = id.ToLowerInvariant() switch
            {
                "seraphleveling" => ("native-stats", "combined walkspeed, maxhealthExtraPoints, hungerrate and healingeffectivness; native action timing retained"),
                "carryon" or "cartcarryonpatch" => ("native-stats", "preserve external walkspeed/hungerrate sources and native inventory behavior; no guessed cargo debit"),
                "jaunt" => (NativeVehicleBridge.JauntReady ? "hooks-ready" : "observe-only", "validated native and Jaunt jump/flight control; animal speed/stamina and equipment remain external"),
                "equusferus" => (NativeVehicleBridge.NativeReady ? "hooks-ready" : "observe-only", "accepted native seat control only; animal speed/stamina and equipment remain external"),
                "cartwrightscaravan" => (NativeVehicleBridge.NativeReady ? "hooks-ready" : "observe-only", "human gait work, combined player walking speed, native permission and hand gates; passengers excluded"),
                "shipwright" => (NativeVehicleBridge.ShipReady ? "hooks-ready" : "observe-only", "validated controller/paddle contract; manual thrust and steering only; sails/inertia retained"),
                "walkableentities" => ("observe-only", "platform motion is excluded; no player propulsion event"),
                _ => ("", "")
            };
            if (status.Length > 0) list.Add(new(id, version, status, evidence));
        }
        return list.ToArray();
    }
}
