using Seraph.Core.Framework;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private FrameworkState ExposeIngestion(FrameworkState frame, IReadOnlyDictionary<string, double> loads, double generic, double amount)
    {
        var agents = PathogenLoads.WithGenericEnteric(loads, generic);
        var incoming = agents.Where(p => p.Value > 0 && Pathogens.Enteric(p.Key))
            .ToDictionary(p => p.Key, p => ExposureBarriers.ContactUnits(p.Value, amount));
        return ExposureBarriers.Add(frameworkKernel, frame, ExposureRoute.Ingestion, incoming, "verified-ingestion");
    }
}
