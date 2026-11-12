using System.Threading.RateLimiting;

namespace TenantRateLimiter.Tests;

public class TokenBucketTests
{
    private static int TakeOneEach(TokenBucket b, int requests, double now)
    {
        int ok = 0;
        for (int i = 0; i < requests; i++)
        {
            if (b.TryTake(1, now))
            {
                ok++;
            }
        }
        return ok;
    }

    [Fact]
    public void The_paper_example()
    {
        // Capacity 5, refill 1 per second, starts full at time 0.
        var b = new TokenBucket(5, 1, 0);

        Assert.Equal(3, TakeOneEach(b, 3, 0));   // 5 -> 2
        Assert.Equal(2, b.Tokens);
        Assert.Equal(2, TakeOneEach(b, 3, 0));   // 2 pass, 1 gets a 429
        Assert.Equal(0, b.Tokens);

        b.RefillTo(1);
        Assert.Equal(1, b.Tokens);               // 0 + 1
        b.RefillTo(4);
        Assert.Equal(4, b.Tokens);               // 1 + 3
        b.RefillTo(10);
        Assert.Equal(5, b.Tokens);               // min(5, 4 + 6)

        Assert.Equal(5, TakeOneEach(b, 7, 10));  // 5 pass, 2 get a 429
        Assert.Equal(1, b.SecondsUntil(1));      // (1 - 0) / 1
    }

    [Fact]
    public void A_bigger_cost_waits_for_the_difference()
    {
        var b = new TokenBucket(5, 1, 0);
        TakeOneEach(b, 5, 0);
        b.RefillTo(1);

        Assert.False(b.TryTake(3, 1));
        Assert.Equal(2, b.SecondsUntil(3));      // (3 - 1) / 1
        Assert.True(b.TryTake(3, 3));
    }

    [Fact]
    public void A_cost_above_capacity_never_fits()
    {
        var b = new TokenBucket(5, 1, 0);

        Assert.True(b.Fits(5));
        Assert.False(b.Fits(6));
        Assert.False(b.TryTake(6, 1_000_000));
    }

    [Fact]
    public void An_older_time_does_not_refill_or_move_the_clock_back()
    {
        var b = new TokenBucket(5, 1, 0);
        TakeOneEach(b, 5, 10);

        b.RefillTo(8);
        Assert.Equal(0, b.Tokens);
        b.RefillTo(11);
        Assert.Equal(1, b.Tokens);               // one second after 10, not three after 8
    }

    [Fact]
    public void Bad_settings_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(5, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(double.NaN, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(5, double.PositiveInfinity, 0));
    }

    // The slow, obvious version: walk the clock forward in steps of 1/1024 s and add
    // rate/1024 each step. Steps of 1/1024 keep every number exact in a double, so the
    // two versions should agree exactly, not just roughly.
    private sealed class SteppedBucket(int capacity, int ratePerSecond)
    {
        public double Tokens { get; private set; } = capacity;
        private long _step;

        public bool TryTake(int cost, long step)
        {
            for (; _step < step; _step++)
            {
                Tokens = Math.Min(capacity, Tokens + (ratePerSecond / 1024.0));
            }
            if (cost > Tokens)
            {
                return false;
            }
            Tokens -= cost;
            return true;
        }
    }

    [Fact]
    public void Lazy_refill_matches_a_bucket_that_refills_step_by_step()
    {
        var random = new Random(20231116);
        for (int trial = 0; trial < 300; trial++)
        {
            int capacity = random.Next(1, 11);
            int rate = random.Next(1, 11);
            var fast = new TokenBucket(capacity, rate, 0);
            var slow = new SteppedBucket(capacity, rate);

            long step = 0;
            for (int i = 0; i < 200; i++)
            {
                // Often several at the same moment (a burst), sometimes a long gap.
                step += random.Next(3) == 0 ? 0 : random.Next(0, 3 * 1024);
                int cost = random.Next(1, capacity + 3);   // sometimes more than capacity

                bool expected = slow.TryTake(cost, step);
                bool actual = fast.TryTake(cost, step / 1024.0);

                Assert.True(expected == actual, $"trial {trial}, request {i}: stepped {expected}, lazy {actual}");
                Assert.Equal(slow.Tokens, fast.Tokens);
            }
        }
    }

    // .NET's TokenBucketRateLimiter refills from the real clock, so these two tests only
    // use moments where no refill happens: everything at one instant.
    private static TokenBucketRateLimiter DotnetBucket(int capacity) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = capacity,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = false,
        QueueLimit = 0,
    });

    [Fact]
    public void A_burst_on_a_full_bucket_matches_dotnet()
    {
        int[] costs = [2, 2, 2, 1, 1, 3, 1];
        using var dotnet = DotnetBucket(5);
        var ours = new TokenBucket(5, 1, 0);

        bool[] expected = costs.Select(c => dotnet.AttemptAcquire(c).IsAcquired).ToArray();
        bool[] actual = costs.Select(c => ours.TryTake(c, 0)).ToArray();

        Assert.Equal([true, true, false, true, false, false, false], expected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_cost_above_capacity_is_refused_up_front_like_dotnet()
    {
        using var dotnet = DotnetBucket(5);
        var ours = new TokenBucket(5, 1, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => dotnet.AttemptAcquire(6));
        Assert.False(ours.Fits(6));
    }
}
