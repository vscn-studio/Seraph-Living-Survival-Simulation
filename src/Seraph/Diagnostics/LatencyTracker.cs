namespace Seraph.Diagnostics;

/// <summary>One outstanding request, monotonic timestamps, no cross-machine clock subtraction.</summary>
internal sealed class LatencyTracker
{
    private long sequence, pending;
    private string epoch = "";
    private double sent, next;
    internal long Begin(string session, double now)
    {
        if (pending != 0 || now < next || string.IsNullOrEmpty(session)) return 0;
        epoch = session; sent = now; next = now + 1000; return pending = ++sequence;
    }
    internal bool Expire(double now, out double elapsed)
    {
        elapsed = now - sent;
        if (pending == 0 || elapsed < 5000) return false;
        pending = 0; return true;
    }
    internal bool Complete(long id, string session, double received, double applied, double serverQueue, out double rtt)
    {
        rtt = received - sent;
        if (pending == 0 || pending != id || session != epoch || !double.IsFinite(rtt) || rtt < 0
            || !double.IsFinite(applied) || applied < received || !double.IsFinite(serverQueue) || serverQueue < 0) return false;
        pending = 0; return true;
    }
    internal void Clear() { pending = 0; epoch = ""; next = 0; }
}
