using System.Collections.Concurrent;
using System.Diagnostics;

namespace Seraph.Application;

public sealed class InboundGate(double capacity = 10, double refillPerSecond = 5)
{
    private sealed class Bucket(double tokens) { public double Tokens = tokens; public long Updated = Stopwatch.GetTimestamp(); }
    private readonly ConcurrentDictionary<string, Bucket> buckets = new();

    public bool TryAccept(string uid)
    {
        var bucket = buckets.GetOrAdd(uid, _ => new Bucket(capacity));
        lock (bucket)
        {
            var now = Stopwatch.GetTimestamp();
            bucket.Tokens = Math.Min(capacity, bucket.Tokens + Stopwatch.GetElapsedTime(bucket.Updated, now).TotalSeconds * refillPerSecond);
            bucket.Updated = now;
            if (bucket.Tokens < 1) return false;
            bucket.Tokens--;
            return true;
        }
    }

    public void Remove(string uid) => buckets.TryRemove(uid, out _);
}
