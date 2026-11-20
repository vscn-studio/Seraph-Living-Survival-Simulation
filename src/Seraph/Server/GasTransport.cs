using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal interface IGasTransport : IDisposable
{
    bool Ready { get; }
    AirChemistry Sample(Vec3d eye, int dimension);
}

internal static class GasTransport
{
    internal static IGasTransport? Create(ICoreServerAPI api, ChunkPollutionStore? pollution, IServerNetworkChannel channel)
    {
        try
        {
            if (pollution is null) return null;
            var realSmoke = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "RealSmoke.ModSystem");
            if (realSmoke?.GetType().Assembly.GetName().Name != "RealSmoke") return null;
            var type = typeof(GasTransport).Assembly.GetType("Seraph.Server.RealSmokeGasTransport")
                ?? throw new NotSupportedException("Build does not include the Real Smoke transport bridge");
            return (IGasTransport)Activator.CreateInstance(type,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [api, pollution, channel], null)!;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[vsslssmod] Gas transport unavailable; new gas exposure disabled: {0}", ex.GetBaseException().Message);
            return null;
        }
    }
}
