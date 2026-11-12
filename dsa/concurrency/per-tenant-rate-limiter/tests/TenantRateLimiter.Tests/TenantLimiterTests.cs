namespace TenantRateLimiter.Tests;

public class TenantLimiterTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // 60 per minute = refill 1 per second, capacity 60 with the default one-minute burst.
    private static TenantLimiter Limiter(double rpm, double itpm) => new(_ => new TenantLimits(rpm, itpm));

    [Fact]
    public void One_customers_burst_does_not_touch_another()
    {
        var limiter = Limiter(5, 1_000_000);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("a", 10, S(0)).Allowed);
        }
        Assert.Equal(Outcome.Limited, limiter.TryAcquire("a", 10, S(0)).Outcome);

        Assert.True(limiter.TryAcquire("b", 10, S(0)).Allowed);
        Assert.Equal(2, limiter.Count);
    }

    [Fact]
    public void Both_or_neither()
    {
        // Room for 2 requests and 10 input tokens.
        var limiter = Limiter(2, 10);

        Assert.True(limiter.TryAcquire("a", 8, S(0)).Allowed);           // 1 request and 2 tokens left
        Assert.False(limiter.TryAcquire("a", 8, S(0)).Allowed);          // not enough tokens
        // If the failed request had still taken a request, there'd be none left for this one.
        Assert.True(limiter.TryAcquire("a", 2, S(0)).Allowed);
    }

    [Fact]
    public void Retry_after_is_the_longer_of_the_two_waits()
    {
        // 60 requests and 600 tokens per minute: refill 1 request and 10 tokens per second.
        var limiter = Limiter(60, 600);
        Assert.True(limiter.TryAcquire("a", 600, S(0)).Allowed);         // tokens bucket now empty

        var d = limiter.TryAcquire("a", 300, S(0));

        Assert.Equal(Outcome.Limited, d.Outcome);
        Assert.Equal(S(30), d.RetryAfter);                               // (300 - 0) / 10, longer than 0 for requests
        Assert.True(limiter.TryAcquire("a", 300, S(30)).Allowed);
    }

    [Fact]
    public void A_prompt_bigger_than_the_bucket_is_too_big_and_takes_nothing()
    {
        var limiter = Limiter(2, 1000);

        Assert.Equal(Outcome.TooBig, limiter.TryAcquire("a", 1001, S(0)).Outcome);
        Assert.True(limiter.TryAcquire("a", 1000, S(0)).Allowed);
        Assert.True(limiter.TryAcquire("a", 0, S(0)).Allowed);           // both requests were still there
    }

    [Fact]
    public void A_short_burst_window_gives_a_smaller_bucket()
    {
        // 60 per minute, but only 10 seconds of it at once: capacity 10, same refill.
        var limiter = new TenantLimiter(_ => new TenantLimits(60, 1_000_000, S(10)));

        int allowed = Enumerable.Range(0, 20).Count(_ => limiter.TryAcquire("a", 1, S(0)).Allowed);

        Assert.Equal(10, allowed);
    }

    [Fact]
    public void Many_threads_on_one_customer_never_get_more_than_the_bucket_holds()
    {
        // Everything at one moment, so nothing refills: exactly 1000 may pass.
        var limiter = Limiter(1000, 3000);
        int allowed = 0;

        Parallel.For(0, 20_000, _ =>
        {
            if (limiter.TryAcquire("a", 3, S(5)).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(1000, allowed);
    }

    [Fact]
    public void Idle_customers_are_removed_once_their_buckets_are_full_again()
    {
        var limiter = Limiter(60, 600);
        limiter.TryAcquire("busy", 600, S(0));
        limiter.TryAcquire("idle", 1, S(0));

        Assert.Equal(1, limiter.RemoveIdle(S(10)));                      // idle is full again by 10 s, busy isn't
        Assert.Equal(1, limiter.Count);
        Assert.Equal(1, limiter.RemoveIdle(S(60)));                      // busy is full after 60 s
        Assert.Equal(0, limiter.Count);

        // Coming back gets a fresh, full bucket, the same as if it had never been removed.
        Assert.True(limiter.TryAcquire("idle", 600, S(61)).Allowed);
    }

    [Fact]
    public async Task Removing_idle_customers_while_requests_arrive_never_lets_extra_through()
    {
        var limiter = Limiter(500, 1_000_000);
        int allowed = 0;
        using var done = new CancellationTokenSource();

        var cleaner = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                limiter.RemoveIdle(S(1));
            }
        });

        Parallel.For(0, 10_000, _ =>
        {
            if (limiter.TryAcquire("a", 1, S(1)).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });
        done.Cancel();
        await cleaner;

        Assert.Equal(500, allowed);
    }

    [Fact]
    public void Bad_input_is_rejected()
    {
        var limiter = Limiter(60, 600);

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire("a", -1, S(0)));
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!, 1, S(0)));
        Assert.Throws<InvalidOperationException>(() => new TenantLimiter(_ => null!).TryAcquire("a", 1, S(0)));
    }

    // A fixed window: count per clock minute, reset to zero when the minute changes.
    private sealed class FixedWindow(int perMinute)
    {
        private long _minute = -1;
        private int _used;

        public bool TryTake(double seconds)
        {
            long minute = (long)Math.Floor(seconds / 60);
            if (minute != _minute)
            {
                _minute = minute;
                _used = 0;
            }
            if (_used == perMinute)
            {
                return false;
            }
            _used++;
            return true;
        }
    }

    [Fact]
    public void A_fixed_window_lets_twice_the_limit_through_around_the_reset()
    {
        // 5 per minute. 5 requests at 0:59 and 5 more at 1:00, one second apart.
        double[] times = [59, 59, 59, 59, 59, 60, 60, 60, 60, 60];

        var window = new FixedWindow(5);
        var limiter = Limiter(5, 1_000_000);

        int windowAllowed = times.Count(window.TryTake);
        int bucketAllowed = times.Count(t => limiter.TryAcquire("a", 1, S(t)).Allowed);

        Assert.Equal(10, windowAllowed);   // the counter reset at 1:00
        Assert.Equal(5, bucketAllowed);    // one second only brings back 5/60 of a token
    }
}
