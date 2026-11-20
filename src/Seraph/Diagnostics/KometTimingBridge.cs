using System.Reflection;
using Vintagestory.API.Common;

namespace Seraph.Diagnostics;

/// <summary>Optional public Komet API only. No assembly dependency or private capture controls.</summary>
internal static class KometTimingBridge
{
    private static readonly object sync = new();
    private static Func<bool>? capturing;
    private static ITimer? timer;
    internal static bool Available => capturing is not null;
    internal static bool Capturing => capturing?.Invoke() == true;

    internal static void Initialize(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("komet")) return;
        lock (sync)
        {
            if (Available) return;
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Komet.Api.KometDebug", false)).FirstOrDefault(t => t is not null);
            if (type is null) return; // Retry at world initialization, after optional mods load.
            try { Bind(type); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TargetInvocationException or MissingMethodException)
            { api.Logger.Warning("[vsslssmod] Komet timing API unavailable: {0}", ex.Message); }
        }
    }

    internal static void Bind(Type type)
    {
        var getter = type.GetProperty("Capturing", BindingFlags.Public | BindingFlags.Static)?.GetMethod
            ?? throw new MissingMethodException("KometDebug.Capturing");
        var method = type.GetMethod("Measure", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new MissingMethodException("KometDebug.Measure");
        var factory = typeof(KometTimingBridge).GetMethod(nameof(CreateMeasure), BindingFlags.NonPublic | BindingFlags.Static)!;
        var bound = (ITimer)factory.MakeGenericMethod(method.ReturnType).Invoke(null, [method])!;
        timer = bound;
        capturing = getter.CreateDelegate<Func<bool>>();
    }

    private static ITimer CreateMeasure<T>(MethodInfo method) where T : struct, IDisposable
        => new Timer<T>(method.CreateDelegate<System.Func<string, T>>());

    internal interface ITimer
    {
        Scope Begin(string name);
        void End(int index);
    }
    private sealed class Timer<T>(System.Func<string, T> start) : ITimer where T : struct, IDisposable
    {
        [ThreadStatic] private static T[]? scopes;
        [ThreadStatic] private static int depth;
        public Scope Begin(string name)
        {
            // Synchronous using scopes on the same thread. Bounded storage; no boxed structs.
            scopes ??= new T[32];
            if (depth == scopes.Length) return default;
            var index = depth++;
            scopes[index] = start(name);
            return new(this, index);
        }
        public void End(int index)
        {
            scopes![index].Dispose(); scopes[index] = default; depth = index;
        }
    }
    internal readonly struct Scope(ITimer? owner, int index) : IDisposable
    {
        public void Dispose() => owner?.End(index);
    }
    internal static Scope Begin(string name) => Capturing && timer is { } active ? active.Begin(name) : default;
}
