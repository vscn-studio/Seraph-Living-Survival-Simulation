using System.Reflection;
using System.Reflection.Emit;

internal static class MotorChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly mod)
    {
        foreach (var name in new[] { "Seraph.Server.DrowsyMotorBridge", "Seraph.Client.DrowsyDreamCamera" })
            if (mod.GetType(name) is not null) throw new Exception("Obsolete dream/movement implementation still present.");
        var api = Assembly.Load("VintagestoryAPI");
        var player = Activator.CreateInstance(api.GetType("Vintagestory.API.Common.EntityPlayer", true)!)!;
        var controls = Activator.CreateInstance(api.GetType("Vintagestory.API.Common.EntityControls", true)!)!;
        var entityType = api.GetType("Vintagestory.API.Common.Entities.Entity", true)!;
        entityType.GetField("OnGround")!.SetValue(player, true);
        controls.GetType().GetProperty("Forward")!.SetValue(controls, true);
        NativeGround(mod, api, player, controls, entityType);
    }

    // Execute the real ground module rather than just asserting our rotation helper.
    // Stub the unrelated capability lookup and walkspeed, retaining actual native acceleration/drag.
    private static void NativeGround(Assembly mod, Assembly api, object player, object controls, Type entityType)
    {
        Type Api(string name) => api.GetType("Vintagestory.API." + name, true)!;
        var harmonyType = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony", true)!;
        var methodType = Assembly.Load("0Harmony").GetType("HarmonyLib.HarmonyMethod", true)!;
        var harmony = Activator.CreateInstance(harmonyType, ["vsslssmod.tests.native-ground"])!;
        var action = mod.GetType("Seraph.Server.NativeInjuryActionBridge", true)!;
        var functionType = mod.GetType("Seraph.Network.FunctionSnapshot", true)!;
        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GroundPhysicsPrefixes"), AssemblyBuilderAccess.Run);
        var testType = testAssembly.DefineDynamicModule("Main").DefineType("GroundPrefixes", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var capabilityPrefix = testType.DefineMethod("Capabilities", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), [functionType.MakeByRefType()]);
        capabilityPrefix.DefineParameter(1, ParameterAttributes.None, "__result");
        var il = capabilityPrefix.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, functionType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Callvirt, functionType.GetProperty("Movement")!.SetMethod!);
        il.Emit(OpCodes.Stind_Ref); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        var speedPrefix = testType.DefineMethod("Speed", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), [typeof(double).MakeByRefType()]);
        speedPrefix.DefineParameter(1, ParameterAttributes.None, "__result");
        il = speedPrefix.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R8, 1d);
        il.Emit(OpCodes.Stind_R8); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        var prefixType = testType.CreateType()!;
        void Patch(MethodInfo target, MethodInfo prefix) => harmonyType.GetMethod("Patch")!.Invoke(harmony,
            [target, Activator.CreateInstance(methodType, [prefix]), null, null, null]);
        using var owner = (IDisposable)Activator.CreateInstance(action, true)!;
        try
        {
            Patch(action.GetMethod("Functions", All)!, prefixType.GetMethod("Capabilities")!);
            Patch(player.GetType().GetMethod("GetWalkSpeedMultiplier")!, prefixType.GetMethod("Speed")!);
            var block = Activator.CreateInstance(Api("Common.Block"))!;
            block.GetType().GetField("DragMultiplier")!.SetValue(block, 1f);
            var accessor = RoutingProxy.Make(Api("Common.IBlockAccessor"), (m, _) => m.Name == "GetBlockRaw" ? block : null);
            var world = RoutingProxy.Make(Api("Common.IWorldAccessor"), (m, _) => m.Name switch {
                "get_ElapsedMilliseconds" => 10000L, "get_BlockAccessor" => accessor,
                _ => m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null
            });
            entityType.GetField("World")!.SetValue(player, world);
            var walk = controls.GetType().GetField("WalkVector")!.GetValue(controls)!;
            walk.GetType().GetField("X")!.SetValue(walk, 0d); walk.GetType().GetField("Z")!.SetValue(walk, .1d);
            (double X, double Z) Apply()
            {
                var position = Activator.CreateInstance(Api("Common.Entities.EntityPos"))!;
                var module = Activator.CreateInstance(Api("Common.Entities.PModuleOnGround"))!;
                module.GetType().GetMethod("DoApply")!.Invoke(module, [.1f, player, position, controls]);
                var motion = position.GetType().GetField("Motion")!.GetValue(position)!;
                return ((double)motion.GetType().GetField("X")!.GetValue(motion)!, (double)motion.GetType().GetField("Z")!.GetValue(motion)!);
            }
            var normal = Apply();
            for (var i = 0; i < 20; i++)
            {
                var next = Apply();
                if (normal.X != 0 || normal.Z <= 0 || next != normal)
                    throw new Exception("Native forward motion changed across frames.");
                if ((double)walk.GetType().GetField("X")!.GetValue(walk)! != 0 || (double)walk.GetType().GetField("Z")!.GetValue(walk)! != .1)
                    throw new Exception("Native input vector was modified.");
            }
            Console.WriteLine("PASS real PModuleOnGround.DoApply: forward stays forward; movement vectors remain native; dream hooks removed");
        }
        finally { harmonyType.GetMethod("UnpatchAll")!.Invoke(harmony, ["vsslssmod.tests.native-ground"]); }
    }
}
