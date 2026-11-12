namespace TenantRateLimiter;

// One token bucket. Holds at most Capacity tokens and gets RefillPerSecond back every second.
// No timer: the refill is worked out when someone looks (lazy refill).
// Not thread-safe on its own. TenantLimiter locks around it.
public sealed class TokenBucket
{
    private double _lastRefill;

    // Starts full, at startSeconds.
    public TokenBucket(double capacity, double refillPerSecond, double startSeconds)
    {
        if (!(capacity > 0) || double.IsInfinity(capacity))
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be above 0");
        }
        if (!(refillPerSecond > 0) || double.IsInfinity(refillPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(refillPerSecond), "refill rate must be above 0");
        }

        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
        Tokens = capacity;
        _lastRefill = startSeconds;
    }

    public double Capacity { get; }
    public double RefillPerSecond { get; }
    public double Tokens { get; private set; }
    public bool IsFull => Tokens >= Capacity;

    // tokens = min(capacity, tokens + seconds passed * rate)
    public void RefillTo(double nowSeconds)
    {
        // Two threads can read the clock, then take the lock in the other order.
        // So an older time can show up here. Ignore it rather than run the clock backwards.
        if (nowSeconds <= _lastRefill)
        {
            return;
        }

        Tokens = Math.Min(Capacity, Tokens + ((nowSeconds - _lastRefill) * RefillPerSecond));
        _lastRefill = nowSeconds;
    }

    // A cost above capacity can never be paid, however long you wait.
    public bool Fits(double cost) => cost <= Capacity;

    public bool CanTake(double cost) => cost <= Tokens;

    public void Take(double cost)
    {
        if (!CanTake(cost))
        {
            throw new InvalidOperationException($"only {Tokens} tokens, can't take {cost}");
        }
        Tokens -= cost;
    }

    // wait = (cost - tokens) / rate. Only meaningful when the cost fits.
    public double SecondsUntil(double cost) => CanTake(cost) ? 0 : (cost - Tokens) / RefillPerSecond;

    // The single-bucket version of the whole thing: refill, then take if there's enough.
    public bool TryTake(double cost, double nowSeconds)
    {
        RefillTo(nowSeconds);
        if (!CanTake(cost))
        {
            return false;
        }
        Take(cost);
        return true;
    }
}
