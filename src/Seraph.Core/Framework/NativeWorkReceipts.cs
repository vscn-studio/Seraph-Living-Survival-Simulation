namespace Seraph.Core.Framework;

/// <summary>Session-local replay and elapsed-time bounds for client-executed native work.</summary>
public sealed class NativeWorkReceipts
{
    private long sequence;
    private double lastTime = double.NaN, available = .35, lastAttack = double.NegativeInfinity;
    public double Accept(long next, double now, double seconds, bool attack)
    {
        if (next <= sequence || !double.IsFinite(now) || !double.IsFinite(seconds) || seconds <= 0 || seconds > .5) return 0;
        sequence = next;
        if (!double.IsNaN(lastTime) && now < lastTime) return 0;
        available = Math.Min(.5, available + (double.IsNaN(lastTime) ? 0 : now - lastTime));
        lastTime = now;
        if (attack)
        {
            if (now - lastAttack < .25) return 0;
            lastAttack = now;
            return 1;
        }
        var paid = Math.Min(seconds, available);
        available -= paid;
        return paid;
    }
}
