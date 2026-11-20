using System.Reflection;

namespace Seraph.Core;

public static class BuildInfo
{
    public static string Version { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
