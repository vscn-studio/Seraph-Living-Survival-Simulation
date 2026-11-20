using Seraph.Core.Framework;
using Seraph.Core.State;

namespace Seraph.Application;

/// <summary>Project contracts, not Vintage Story APIs. Unknown units never enter physical integration.</summary>
public sealed record AdapterEvidence(string AdapterId, string ProviderVersion, bool UnitsVerified, string Evidence);
public sealed record RoomSample(bool Enclosed, double WindAttenuation, double RadiantDeltaC, AdapterEvidence Evidence);
public sealed record ExposureSample(string EventId, string Pathogen, string Route, double HazardPerBioSecond, AdapterEvidence Evidence);
public sealed record InjurySample(string EventId, WoundKind Kind, string Region, double Severity, double BleedLps, AdapterEvidence Evidence);

public interface IRoomEnvironmentAdapter
{
    bool TrySample(string playerUid, out RoomSample sample);
}
public interface IExposureAdapter
{
    IEnumerable<ExposureSample> Sample(string playerUid, double bioSeconds);
}
/// <summary>Optional damage-source contract for providers with an explicit server hit zone.</summary>
public interface IVerifiedInjuryLocation
{
    string InjuryRegion { get; }
}

public interface IInjuryAdapter
{
    bool TryResolve(string serverDamageEvent, out InjurySample injury);
}
public interface IDownedDelegate
{
    string ProviderId { get; }
    bool OwnsRevival { get; }
    bool IsDowned(string playerUid);
    bool TryEnter(string playerUid);
    bool TryRescue(string playerUid, string? reviverUid);
}

/// <summary>Explicit food portion data. Saturation-to-energy conversion is never inferred here.</summary>
public sealed record FoodPortion(string Code, double WaterLPerServing, double EnergyKJPerServing,
    double MassGPerServing, AdapterEvidence Evidence)
{
    private static bool In(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    public bool IsVerified => !string.IsNullOrWhiteSpace(Code) && Evidence.UnitsVerified
        && In(WaterLPerServing, 0, 10) && In(EnergyKJPerServing, 0, 100000)
        && In(MassGPerServing, double.Epsilon, 10000);
    public IntakeReceipt? Receipt(string eventId, double actualServings) => IsVerified
        && In(actualServings, double.Epsilon, 1)
        ? new(eventId, "verified-food:" + Code, WaterLPerServing * actualServings,
            EnergyKJPerServing * actualServings, 0, true, false) : null;
}
