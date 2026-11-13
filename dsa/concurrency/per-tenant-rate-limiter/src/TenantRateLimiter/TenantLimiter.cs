using System.Collections.Concurrent;

namespace TenantRateLimiter;

// A customer's limits, the way LLM APIs write them: requests per minute and input tokens per minute.
// Burst is how much of that limit can be used at once: the bucket holds Burst's worth of the limit.
// With the default of one minute, capacity = the per-minute limit and it refills over a minute.
public sealed record TenantLimits(double RequestsPerMinute, double InputTokensPerMinute, TimeSpan Burst)
{
    public TenantLimits(double requestsPerMinute, double inputTokensPerMinute)
        : this(requestsPerMinute, inputTokensPerMinute, TimeSpan.FromMinutes(1))
    {
    }

    public double RequestCapacity => RequestsPerMinute * Burst.TotalSeconds / 60;
    public double InputTokenCapacity => InputTokensPerMinute * Burst.TotalSeconds / 60;
}

public enum Outcome
{
    Allowed,
    Limited,  // try again after RetryAfter (HTTP 429)
    TooBig,   // bigger than the bucket, so waiting won't help
}

public readonly record struct Decision(Outcome Outcome, TimeSpan RetryAfter)
{
    public bool Allowed => Outcome == Outcome.Allowed;
}

// One pair of buckets per customer, kept in a map keyed by customer id.
// A request costs 1 from the requests bucket and its prompt size from the input-tokens bucket.
// It gets through only if both have enough, and then it takes from both.
// Each customer has its own lock, so customers never wait on each other.
public sealed class TenantLimiter
{
    private readonly Func<string, TenantLimits> _limitsFor;
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new();
    private readonly TimeProvider _clock;
    private readonly long _started;

    public TenantLimiter(Func<string, TenantLimits> limitsFor, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(limitsFor);
        _limitsFor = limitsFor;
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
    }

    public int Count => _tenants.Count;

    // For a real server: time comes from a monotonic clock, not DateTime.Now, which can jump.
    public Decision TryAcquire(string tenant, int inputTokens) =>
        TryAcquire(tenant, inputTokens, _clock.GetElapsedTime(_started));

    // Time is passed in, so tests and the trace replay decide what "now" is.
    public Decision TryAcquire(string tenant, int inputTokens, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        double seconds = now.TotalSeconds;

        while (true)
        {
            // GetOrAdd can run the factory twice when two threads add the same customer at once.
            // Only one result is kept and the other is thrown away, which is fine here.
            var t = _tenants.GetOrAdd(tenant, NewTenant, seconds);

            lock (t.Gate)
            {
                // RemoveIdle dropped this one after we found it. Go round and get the new one.
                if (t.Removed)
                {
                    continue;
                }

                t.Requests.RefillTo(seconds);
                t.InputTokens.RefillTo(seconds);

                if (!t.Requests.Fits(1) || !t.InputTokens.Fits(inputTokens))
                {
                    return new Decision(Outcome.TooBig, TimeSpan.Zero);
                }

                // Both or neither: check both before taking from either.
                if (t.Requests.CanTake(1) && t.InputTokens.CanTake(inputTokens))
                {
                    t.Requests.Take(1);
                    t.InputTokens.Take(inputTokens);
                    return new Decision(Outcome.Allowed, TimeSpan.Zero);
                }

                // Wait for whichever bucket needs longer.
                double wait = Math.Max(t.Requests.SecondsUntil(1), t.InputTokens.SecondsUntil(inputTokens));
                return new Decision(Outcome.Limited, TimeSpan.FromSeconds(wait));
            }
        }
    }

    // For a real server: the same clock as TryAcquire(tenant, inputTokens).
    public int RemoveIdle() => RemoveIdle(_clock.GetElapsedTime(_started));

    // A full bucket is the same as a new one, so a customer whose buckets have both refilled
    // can be dropped without changing any future answer. That keeps the map from growing forever.
    public int RemoveIdle(TimeSpan now)
    {
        double seconds = now.TotalSeconds;
        int removed = 0;
        foreach (var (id, t) in _tenants)
        {
            lock (t.Gate)
            {
                // Another RemoveIdle running at the same time already dropped it.
                if (t.Removed)
                {
                    continue;
                }

                t.Requests.RefillTo(seconds);
                t.InputTokens.RefillTo(seconds);
                if (t.Requests.IsFull && t.InputTokens.IsFull)
                {
                    t.Removed = true;
                    _tenants.TryRemove(new KeyValuePair<string, Tenant>(id, t));
                    removed++;
                }
            }
        }
        return removed;
    }

    private Tenant NewTenant(string id, double nowSeconds)
    {
        var limits = _limitsFor(id) ?? throw new InvalidOperationException($"no limits for '{id}'");

        // A bucket that can't hold one request would turn every request away as too big.
        if (limits.RequestCapacity < 1)
        {
            throw new InvalidOperationException(
                $"'{id}': a burst of {limits.Burst.TotalSeconds} s holds {limits.RequestCapacity:0.##} requests, less than 1");
        }
        return new Tenant(
            new TokenBucket(limits.RequestCapacity, limits.RequestsPerMinute / 60, nowSeconds),
            new TokenBucket(limits.InputTokenCapacity, limits.InputTokensPerMinute / 60, nowSeconds));
    }

    private sealed class Tenant(TokenBucket requests, TokenBucket inputTokens)
    {
        public Lock Gate { get; } = new();
        public TokenBucket Requests { get; } = requests;
        public TokenBucket InputTokens { get; } = inputTokens;
        public bool Removed { get; set; }
    }
}
