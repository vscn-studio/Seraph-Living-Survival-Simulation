using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Gates new human propulsion at native execution points; never rewinds position or changes animal stats.</summary>
internal sealed class NativeVehicleBridge : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.vehicles");
    private static ILogger? logger;
    private static bool nativeMatched;
    internal static bool NativeReady { get; private set; }
    internal static bool ShipReady { get; private set; }
    internal static bool JauntReady { get; private set; }
    private static Type? jauntType;
    private sealed class Permissions
    {
        internal long At;
        internal bool Move, Turn;
        internal FunctionSnapshot? Functions;
    }
    private static ConditionalWeakTable<IMountableSeat, Permissions> permissions = new();
    private static readonly HashSet<string> warned = [];
    private sealed record ShipAdapter(Type Type, System.Func<Entity, long> Controller, System.Func<Entity, bool> Sails,
        System.Func<Entity, Entity, bool> Paddle);
    private static ShipAdapter? ship;

    internal NativeVehicleBridge() : this(null) { }
    internal NativeVehicleBridge(ICoreAPI? api)
    {
        logger = api?.Logger;
        nativeMatched = false;
        NativeReady = Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorRideable), "SeatsToMotion"),
            postfix: nameof(RideEnd), transpiler: nameof(RidePermissions)) && nativeMatched;
        NativeReady &= Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorGait), "Move"), postfix: nameof(GaitEnd));
    }

    internal void ResolveOptional()
    {
        ResolveJaunt();
        if (ship is not null) return;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("shipwright.EntityShipwrightBoat")).FirstOrDefault(t => t is not null);
        if (type is null) return;
        try
        {
            var target = AccessTools.DeclaredMethod(type, "SeatsToMotion", [typeof(float)]);
            var controller = AccessTools.Field(type, "CurrentlyControllingEntityId");
            var sails = AccessTools.Field(type, "unfurlSails");
            var paddle = AccessTools.Method(type, "HasPaddle", [typeof(Entity)]);
            if (target?.ReturnType != typeof(Vec2d) || controller?.FieldType != typeof(long)
                || sails?.FieldType != typeof(bool) || paddle?.ReturnType != typeof(bool))
                throw new NotSupportedException("Shipwright control contract changed");
            var entity = Expression.Parameter(typeof(Entity));
            var rider = Expression.Parameter(typeof(Entity));
            var instance = Expression.Convert(entity, type);
            var adapter = new ShipAdapter(type,
                Expression.Lambda<System.Func<Entity, long>>(Expression.Field(instance, controller), entity).Compile(),
                Expression.Lambda<System.Func<Entity, bool>>(Expression.Field(instance, sails), entity).Compile(),
                Expression.Lambda<System.Func<Entity, Entity, bool>>(Expression.Call(instance, paddle, rider), entity, rider).Compile());
            if (Patch(target, postfix: nameof(ShipEnd))) { ship = adapter; ShipReady = true; }
        }
        catch (Exception ex) { Warn("ship-contract", ex); }
    }

    private void ResolveJaunt()
    {
        if (JauntReady || !NativeReady) return;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Jaunt.Behaviors.EntityBehaviorJauntRideable")).FirstOrDefault(t => t is not null);
        if (type is null) return;
        try
        {
            if (!typeof(EntityBehaviorRideable).IsAssignableFrom(type)) throw new NotSupportedException("Jaunt rideable contract changed");
            var target = AccessTools.DeclaredMethod(type, "SeatsToMotion", [typeof(float)]);
            if (target?.ReturnType != typeof(double)) throw new NotSupportedException("Jaunt control contract changed");
            JauntReady = Patch(target, transpiler: nameof(JauntInputs));
            if (JauntReady) jauntType = type;
        }
        catch (Exception ex) { Warn("jaunt-contract", ex); }
    }

    // Jaunt reads jump/descend again after calling base.SeatsToMotion. Gate those reads,
    // not the raw seat controls: its normal flight/landing logic still clears generated drive.
    private static IEnumerable<CodeInstruction> JauntInputs(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var jump = AccessTools.PropertyGetter(typeof(EntityControls), "Jump");
        var sneak = AccessTools.PropertyGetter(typeof(EntityControls), "Sneak");
        if (code.Count(i => i.Calls(AccessTools.DeclaredMethod(typeof(EntityBehaviorRideable), "SeatsToMotion"))) != 1
            || code.Count(i => i.Calls(jump)) != 4 || code.Count(i => i.Calls(sneak)) != 2)
            throw new NotSupportedException("Jaunt jump/flight input contract did not match");
        var filter = AccessTools.Method(typeof(NativeVehicleBridge), nameof(JauntInput));
        var result = new List<CodeInstruction>();
        foreach (var i in code)
        {
            result.Add(i);
            if (i.Calls(jump) || i.Calls(sneak)) { result.Add(new(OpCodes.Ldarg_0)); result.Add(new(OpCodes.Call, filter)); }
        }
        return result;
    }

    private static bool JauntInput(bool input, EntityBehaviorRideable rideable)
    {
        if (!input || !JauntReady || rideable.Controller is not EntityPlayer player || player.MountedOn is not { } seat) return input;
        return !permissions.TryGetValue(seat, out var state) || state.Functions is not { Manipulation: true, CanUseActiveHand: false };
    }

    internal static bool JauntDescending(Entity entity, EntityControls controls) => JauntReady
        && entity is EntityAgent { Controls.IsFlying: true } && controls.Sneak && !controls.Jump
        && entity.GetBehavior<EntityBehaviorRideable>() is { } rideable && jauntType?.IsInstanceOfType(rideable) == true;

    private bool Patch(MethodInfo? target, string? postfix = null, string? transpiler = null)
    {
        try
        {
            if (target is null) throw new MissingMethodException("Vehicle entry point unavailable");
            harmony.Patch(target, postfix: postfix is null ? null : new(typeof(NativeVehicleBridge), postfix),
                transpiler: transpiler is null ? null : new(typeof(NativeVehicleBridge), transpiler));
            return true;
        }
        catch (Exception ex) { Warn(target?.Name ?? "native-contract", ex); return false; }
    }

    internal static void Warn(string key, Exception ex)
    {
        lock (warned)
            if (warned.Add(key)) logger?.Warning("[vsslssmod] Vehicle adapter {0} degraded: {1}: {2}", key, ex.GetType().Name, ex.Message);
    }

    // Match the two native permission locals immediately before the jump/gait branch.
    // Reject a changed IL contract instead of guessing local slots or replacing native CanRide/CanTurn.
    private static IEnumerable<CodeInstruction> RidePermissions(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var locals = __originalMethod.GetMethodBody()!.LocalVariables;
        static int Index(CodeInstruction i) => i.operand switch { LocalBuilder l => l.LocalIndex, byte b => b, int n => n, _ => -1 };
        var matches = Enumerable.Range(0, Math.Max(0, code.Count - 3)).Where(i =>
            code[i].opcode == OpCodes.Ldloc_S && Index(code[i]) == 7 && code[i + 1].opcode == OpCodes.Brfalse
            && code[i + 2].opcode == OpCodes.Ldloc_S && Index(code[i + 2]) == 6
            && code[i + 3].Calls(AccessTools.PropertyGetter(typeof(EntityControls), "Jump"))).ToArray();
        if (matches.Length != 1 || locals.Count < 9 || locals[4].LocalType != typeof(IMountableSeat)
            || locals[7].LocalType != typeof(bool) || locals[8].LocalType != typeof(bool))
            throw new NotSupportedException("Native rideable permission branch did not match");
        var at = matches[0];
        var injected = new List<CodeInstruction>();
        foreach (var (slot, method) in new[] { (7, nameof(FilterMove)), (8, nameof(FilterTurn)) })
        {
            injected.Add(new(OpCodes.Ldloc_S, (byte)slot));
            injected.Add(new(OpCodes.Ldarg_0));
            injected.Add(new(OpCodes.Ldloc_S, (byte)4));
            injected.Add(new(OpCodes.Call, AccessTools.Method(typeof(NativeVehicleBridge), method)));
            injected.Add(new(OpCodes.Stloc_S, (byte)slot));
        }
        injected[0].labels.AddRange(code[at].labels); code[at].labels.Clear();
        code.InsertRange(at, injected);
        nativeMatched = true;
        return code;
    }

    private static bool FilterMove(bool allowed, EntityBehaviorRideable rideable, IMountableSeat seat)
        => Filter(allowed, rideable, seat, false);
    private static bool FilterTurn(bool allowed, EntityBehaviorRideable rideable, IMountableSeat seat)
        => Filter(allowed, rideable, seat, true);
    private static bool Filter(bool allowed, EntityBehaviorRideable rideable, IMountableSeat seat, bool turn)
    {
        try
        {
            if (seat.Passenger is not EntityPlayer player) return allowed;
            var state = permissions.GetOrCreateValue(seat);
            state.At = rideable.entity.World.ElapsedMilliseconds;
            // Move and turn are evaluated together: resolve body functions once per native control pass.
            if (turn) state.Turn = allowed;
            else { state.Move = allowed; state.Functions = NativeInjuryActionBridge.Functions(player); }
            if (allowed && state.Functions is { Manipulation: true, CanUseActiveHand: false })
            {
                if (seat.Controls.TriesToMove || seat.Controls.Jump) NativeInjuryActionBridge.Denied(player);
                return false;
            }
        }
        catch (Exception ex) { Warn("native-permission", ex); }
        return allowed;
    }

    internal static (bool Move, bool Turn) Accepted(IMountableSeat seat)
        => NativeReady && permissions.TryGetValue(seat, out var state)
            && seat.Entity.World.ElapsedMilliseconds - state.At is >= 0 and <= 500
            ? (state.Move, state.Turn) : (false, false);

    internal static bool IsCart(Entity? entity) => entity?.Code is { Domain: "cartwrightscaravan" } code
        && (code.Path is "cart" or "marketstall" or "sled" || code.Path.StartsWith("cart-", StringComparison.Ordinal)
            || code.Path.StartsWith("marketstall-", StringComparison.Ordinal) || code.Path.StartsWith("sled-", StringComparison.Ordinal));
    internal static bool SprintGait(EntityBehaviorGait gait) => gait.CurrentGait?.Code == "sprint";

    private static void RideEnd(EntityBehaviorRideable __instance, ref double __result)
    {
        try
        {
            if (!NativeReady || __instance.Controller is not EntityPlayer player || player.MountedOn is not { } seat) return;
            var accepted = Accepted(seat);
            var functions = permissions.TryGetValue(seat, out var state) ? state.Functions : null;
            if (functions is not { Manipulation: true }) return;
            __result *= functions.CanUseActiveHand ? HandFactor(functions) : 0;
            if (!IsCart(__instance.entity) || __instance.entity.GetBehavior<EntityBehaviorGait>() is not { CurrentGait: not null } gait) return;
            if (!accepted.Move || functions is { Manipulation: true, CanUseActiveHand: false })
            { if (!gait.IsIdle) gait.SetIdle(); return; }
            if (functions is { CanSprint: false } && SprintGait(gait))
            {
                var walk = gait.Gaits.TryGetValue("walk");
                if (walk is not null) gait.CurrentGait = walk;
                else if (!gait.IsIdle) gait.SetIdle();
            }
        }
        catch (Exception ex) { Warn("ride-output", ex); }
    }

    private static void GaitEnd(EntityBehaviorGait __instance)
    {
        try
        {
            if (!NativeReady || !IsCart(__instance.entity) || __instance.entity is not EntityAgent cart
                || cart.GetBehavior<EntityBehaviorRideable>()?.Controller is not EntityPlayer player
                || player.MountedOn is not { } seat) return;
            if (!permissions.TryGetValue(seat, out var state) || state.Functions is not { Manipulation: true } f) return;
            var factor = Accepted(seat).Move ? NativeStatInputs.Multiplier(player, "walkspeed") : 0;
            if (!f.CanUseActiveHand) factor = 0;
            cart.Controls.WalkVector.X *= factor; cart.Controls.WalkVector.Z *= factor;
            if (cart.Swimming) { cart.Controls.FlyVector.X *= factor; cart.Controls.FlyVector.Z *= factor; }
        }
        catch (Exception ex) { Warn("cart-output", ex); }
    }

    internal static double HandFactor(FunctionSnapshot functions)
        => double.IsFinite(functions.HandCapacity) ? Math.Clamp(functions.HandCapacity, 0, 1) : 1;

    internal static bool IsShip(Entity entity) => entity.Code?.Domain == "shipwright"
        || ship?.Type.IsInstanceOfType(entity) == true;

    internal static bool ShipState(Entity entity, EntityAgent player, out bool sails)
    {
        sails = false;
        try
        {
            if (!ShipReady || ship is not { } adapter || !adapter.Type.IsInstanceOfType(entity)
                || adapter.Controller(entity) != player.EntityId || !adapter.Paddle(entity, player)) return false;
            sails = adapter.Sails(entity) && entity.WatchedAttributes.GetInt("sailPosition") > 0;
            return true;
        }
        catch (Exception ex) { Warn("ship-state", ex); return false; }
    }

    private static void ShipEnd(Entity __instance, float dt, Vec2d __result)
    {
        try
        {
            var controller = __instance.GetBehavior<EntityBehaviorSeatable>()?.Controller as EntityPlayer;
            if (controller?.MountedOn is not { } seat || !ShipState(__instance, controller, out var sails)
                || NativeInjuryActionBridge.Functions(controller) is not { Manipulation: true } f) return;
            var factor = f.CanUseActiveHand ? HandFactor(f) : 0;
            if (factor == 0 && seat.Controls.TriesToMove) NativeInjuryActionBridge.Denied(controller);
            var automatic = sails ? (double)(dt * __instance.WatchedAttributes.GetInt("sailPosition") * 1.5f) : 0;
            __result.X = automatic + (__result.X - automatic) * factor;
            __result.Y *= factor;
        }
        catch (Exception ex) { Warn("ship-output", ex); }
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id); NativeReady = ShipReady = JauntReady = false; ship = null; jauntType = null;
        permissions = new(); logger = null; lock (warned) warned.Clear();
    }
}
