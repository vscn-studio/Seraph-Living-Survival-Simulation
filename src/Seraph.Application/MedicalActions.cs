namespace Seraph.Application;

public enum MedicalActionPhase { Pending, Completed, Cancelled }
public sealed record MedicalAction(string Id, string Actor, string Target, string Action, long LifeGeneration,
    double StartedRealSeconds, double DurationSeconds, double StartX, double StartY, double StartZ, MedicalActionPhase Phase);

/// <summary>Server action transaction: no dose, target or physical values come from the client.</summary>
public sealed class MedicalActionCoordinator
{
    private readonly Dictionary<string, MedicalAction> pending = [];
    public MedicalAction? Get(string actor) => pending.GetValueOrDefault(actor);
    public bool Begin(MedicalAction action)
    {
        if (action.Phase != MedicalActionPhase.Pending || string.IsNullOrWhiteSpace(action.Id)
            || action.Actor != action.Target || action.Action is not ("pressure" or "clean")
            || !double.IsFinite(action.StartedRealSeconds) || !double.IsFinite(action.DurationSeconds)
            || action.DurationSeconds is < 1 or > 30 || !double.IsFinite(action.StartX)
            || !double.IsFinite(action.StartY) || !double.IsFinite(action.StartZ)) return false;
        return pending.TryAdd(action.Actor, action);
    }
    public MedicalAction? Advance(string actor, double realSeconds, long lifeGeneration, bool alive,
        double x, double y, double z, bool triesToMove)
    {
        if (!pending.TryGetValue(actor, out var action)) return null;
        var squared = Math.Pow(x - action.StartX, 2) + Math.Pow(y - action.StartY, 2) + Math.Pow(z - action.StartZ, 2);
        if (!alive || lifeGeneration != action.LifeGeneration || triesToMove || !double.IsFinite(realSeconds)
            || !double.IsFinite(squared) || squared > 0.25 || realSeconds < action.StartedRealSeconds)
        {
            pending.Remove(actor);
            return action with { Phase = MedicalActionPhase.Cancelled };
        }
        if (realSeconds - action.StartedRealSeconds < action.DurationSeconds) return null;
        pending.Remove(actor);
        return action with { Phase = MedicalActionPhase.Completed };
    }
    public void Cancel(string actor) => pending.Remove(actor);
}

/// <summary>Adapters that debit finite inventory must supply durable atomicity, otherwise fail closed.</summary>
public interface IInventoryMedicalTransaction
{
    bool SupportsAtomicDebitAndStateCommit { get; }
    bool TryCommit(string transactionId, string playerUid, string expectedItemCode, int quantity, byte[] candidateState);
}

/// <summary>Only explicit verified portion receipts may enter the core; callback names alone prove no volume.</summary>
public interface IIntakeAdapter
{
    string Id { get; }
    bool TryGetVerifiedReceipt(string serverEventId, out Seraph.Core.Framework.IntakeReceipt receipt);
}
