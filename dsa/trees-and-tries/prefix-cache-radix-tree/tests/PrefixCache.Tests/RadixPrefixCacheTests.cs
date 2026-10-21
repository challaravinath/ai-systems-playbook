using PrefixCache;

namespace PrefixCache.Tests;

internal static class T
{
    public static int[] Seq(params int[] tokens) => tokens;
    public static int[] Range(int start, int count) => Enumerable.Range(start, count).ToArray();

    /// <summary>One whole request: acquire, "compute" the tail, cache it, finish.</summary>
    public static int Serve(RadixPrefixCache cache, int[] prompt)
    {
        using var lease = cache.Acquire(prompt);
        cache.Insert(lease);
        return lease.ReusedLength;
    }
}

public class Matching
{
    [Fact]
    public void Empty_cache_reuses_nothing()
    {
        var cache = new RadixPrefixCache(100);
        using var lease = cache.Acquire(T.Seq(1, 2, 3));
        Assert.Equal(0, lease.ReusedLength);
        Assert.Equal(0, cache.CachedTokens);
    }

    [Fact]
    public void Same_prompt_twice_is_fully_reused()
    {
        var cache = new RadixPrefixCache(100);
        T.Serve(cache, T.Seq(1, 2, 3, 4));
        Assert.Equal(4, T.Serve(cache, T.Seq(1, 2, 3, 4)));
        Assert.Equal(4, cache.CachedTokens);
    }

    [Fact]
    public void Shared_beginning_is_reused_and_stored_once()
    {
        var cache = new RadixPrefixCache(100);
        T.Serve(cache, T.Seq(1, 2, 3, 4));
        Assert.Equal(3, T.Serve(cache, T.Seq(1, 2, 3, 9)));   // [1 2 3] shared, 9 is new
        Assert.Equal(5, cache.CachedTokens);                   // 1 2 3 + 4 + 9
        Assert.Equal(3, cache.Audit().Nodes);                  // [1 2 3] with children [4] and [9]
    }

    [Fact]
    public void Splitting_an_edge_keeps_both_branches()
    {
        var cache = new RadixPrefixCache(100);
        T.Serve(cache, T.Seq(1, 2, 3, 4));
        T.Serve(cache, T.Seq(1, 2, 3, 9));
        Assert.Equal(4, T.Serve(cache, T.Seq(1, 2, 3, 4)));
        Assert.Equal(4, T.Serve(cache, T.Seq(1, 2, 3, 9)));
    }

    [Fact]
    public void A_shorter_prompt_matches_inside_an_edge()
    {
        var cache = new RadixPrefixCache(100);
        T.Serve(cache, T.Seq(1, 2, 3, 4, 5));
        Assert.Equal(2, T.Serve(cache, T.Seq(1, 2)));
        Assert.Equal(5, cache.CachedTokens);
        Assert.Equal(2, cache.Audit().Nodes);                  // [1 2] with child [3 4 5]
    }

    [Fact]
    public void Different_first_token_shares_nothing()
    {
        var cache = new RadixPrefixCache(100);
        T.Serve(cache, T.Seq(1, 2, 3));
        Assert.Equal(0, T.Serve(cache, T.Seq(7, 2, 3)));
        Assert.Equal(6, cache.CachedTokens);
    }

    [Fact]
    public void Empty_prompt_is_allowed()
    {
        var cache = new RadixPrefixCache(10);
        Assert.Equal(0, T.Serve(cache, []));
        Assert.Equal(0, cache.CachedTokens);
    }

    [Fact]
    public void Matches_the_longest_prefix_of_any_cached_prompt()
    {
        // No eviction (huge capacity), so the answer must equal a brute-force search.
        var rng = new Random(7);
        var cache = new RadixPrefixCache(1_000_000);
        var seen = new List<int[]>();
        for (int r = 0; r < 300; r++)
        {
            var prompt = Enumerable.Range(0, rng.Next(0, 12)).Select(_ => rng.Next(0, 3)).ToArray(); // tiny alphabet: lots of sharing
            var expected = seen.Select(s => s.Zip(prompt).TakeWhile(p => p.First == p.Second).Count()).DefaultIfEmpty(0).Max();
            Assert.Equal(expected, T.Serve(cache, prompt));
            seen.Add(prompt);
            cache.Audit();
        }
    }
}

public class Eviction
{
    [Fact]
    public void Never_holds_more_than_capacity()
    {
        var cache = new RadixPrefixCache(10);
        for (int r = 0; r < 20; r++) T.Serve(cache, T.Range(r * 100, 4));
        Assert.True(cache.CachedTokens <= 10);
        cache.Audit();
    }

    [Fact]
    public void Least_recently_used_goes_first()
    {
        var cache = new RadixPrefixCache(8);
        int[] a = T.Range(100, 4), b = T.Range(200, 4), c = T.Range(300, 4);
        T.Serve(cache, a);
        T.Serve(cache, b);
        T.Serve(cache, a);                         // a is now more recent than b
        T.Serve(cache, c);                         // full: someone must go
        Assert.Equal(4, T.Serve(cache, a));        // a survived
        Assert.Equal(4, T.Serve(cache, c));        // c was cached
        Assert.Equal(0, T.Serve(cache, b));        // b was evicted
    }

    [Fact]
    public void A_shared_prefix_outlives_its_branches()
    {
        // [sys a] and [sys b] share "sys". Evicting a branch must not take the shared part with it.
        var cache = new RadixPrefixCache(10);
        int[] sys = [1, 2, 3, 4];
        T.Serve(cache, [.. sys, 10, 11]);
        T.Serve(cache, [.. sys, 20, 21]);          // 4 + 2 + 2 = 8 tokens
        T.Serve(cache, T.Range(900, 2));           // needs 2 more: room for exactly that, nothing evicted
        T.Serve(cache, [.. sys, 30, 31]);          // needs 2: evicts the oldest leaf [10 11]
        Assert.Equal(4, T.Serve(cache, [.. sys, 10, 11]));   // the shared part survived; its old branch did not
        Assert.Equal(6, T.Serve(cache, [.. sys, 30, 31]));
    }

    [Fact]
    public void Evicting_a_last_child_makes_the_parent_evictable()
    {
        var cache = new RadixPrefixCache(6);
        T.Serve(cache, T.Seq(1, 2, 3, 4));
        T.Serve(cache, T.Seq(1, 2, 3, 5));         // [1 2 3] with children [4] and [5]: 5 tokens
        T.Serve(cache, T.Range(100, 6));           // needs all 6: both leaves, then [1 2 3] itself
        Assert.Equal(6, cache.CachedTokens);
        Assert.Equal(0, T.Serve(cache, T.Seq(1, 2, 3)));
    }

    [Fact]
    public void Too_long_to_fit_caches_as_much_as_fits()
    {
        var cache = new RadixPrefixCache(5);
        using var lease = cache.Acquire(T.Range(0, 8));
        Assert.Equal(5, cache.Insert(lease));
        Assert.Equal(5, lease.CachedLength);
        Assert.Equal(5, cache.CachedTokens);
    }
}

public class Pinning
{
    [Fact]
    public void A_prefix_in_use_is_never_evicted()
    {
        var cache = new RadixPrefixCache(8);
        int[] a = T.Range(100, 4);
        T.Serve(cache, a);
        using var running = cache.Acquire(a);       // a running request is using a
        T.Serve(cache, T.Range(200, 4));            // fills the cache
        T.Serve(cache, T.Range(300, 4));            // must evict: only the [200..] prompt may go
        Assert.Equal(4, running.ReusedLength);
        Assert.Equal(4, T.Serve(cache, a));
        cache.Audit();
    }

    [Fact]
    public void When_everything_is_pinned_nothing_new_is_cached()
    {
        var cache = new RadixPrefixCache(4);
        using var running = cache.Acquire(T.Range(0, 4));
        cache.Insert(running);                      // cache full, and all of it pinned
        using var other = cache.Acquire(T.Range(50, 4));
        Assert.Equal(0, cache.Insert(other));
        Assert.Equal(4, cache.CachedTokens);
    }

    [Fact]
    public void Releasing_makes_it_evictable_again()
    {
        var cache = new RadixPrefixCache(4);
        var running = cache.Acquire(T.Range(0, 4));
        cache.Insert(running);
        running.Dispose();                          // request finished
        using var other = cache.Acquire(T.Range(50, 4));
        Assert.Equal(4, cache.Insert(other));
        Assert.Equal(1, cache.Audit().PinnedNodes);   // only `other` is pinned now
    }

    [Fact]
    public void A_split_keeps_the_pin_on_both_halves()
    {
        var cache = new RadixPrefixCache(6);
        using var running = cache.Acquire(T.Seq(1, 2, 3, 4));
        cache.Insert(running);
        T.Serve(cache, T.Seq(1, 2, 9));            // splits [1 2 3 4] into [1 2] and [3 4]
        T.Serve(cache, T.Range(100, 2));           // needs room: may evict [9] only
        Assert.Equal(4, T.Serve(cache, T.Seq(1, 2, 3, 4)));
        cache.Audit();
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var cache = new RadixPrefixCache(10);
        var lease = cache.Acquire(T.Seq(1, 2));
        cache.Insert(lease);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, cache.Audit().PinnedNodes);
    }

    [Fact]
    public void All_pins_are_gone_after_every_request_finishes()
    {
        var rng = new Random(11);
        var cache = new RadixPrefixCache(40);
        var open = new List<PrefixLease>();
        for (int r = 0; r < 500; r++)
        {
            var prompt = Enumerable.Range(0, rng.Next(1, 10)).Select(_ => rng.Next(0, 4)).ToArray();
            var lease = cache.Acquire(prompt);
            cache.Insert(lease);
            open.Add(lease);
            if (open.Count > 3) { var i = rng.Next(open.Count); open[i].Dispose(); open.RemoveAt(i); }
            cache.Audit();
        }
        open.ForEach(l => l.Dispose());
        Assert.Equal(0, cache.Audit().PinnedNodes);
    }
}

public class Requests
{
    [Fact]
    public void Insert_after_another_request_cached_the_same_tail_adds_nothing()
    {
        var cache = new RadixPrefixCache(100);
        using var first = cache.Acquire(T.Seq(1, 2, 3));
        using var second = cache.Acquire(T.Seq(1, 2, 3)); // both arrive before either is cached
        Assert.Equal(3, cache.Insert(first));
        Assert.Equal(0, cache.Insert(second));             // already there: no duplicate
        Assert.Equal(3, cache.CachedTokens);
        Assert.Equal(3, second.CachedLength);
    }

    [Fact]
    public void Insert_with_a_finished_lease_throws()
    {
        var cache = new RadixPrefixCache(10);
        var lease = cache.Acquire(T.Seq(1));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Insert(lease));
    }

    [Fact]
    public void Insert_with_another_caches_lease_throws()
    {
        using var lease = new RadixPrefixCache(10).Acquire(T.Seq(1));
        Assert.Throws<ArgumentException>(() => new RadixPrefixCache(10).Insert(lease));
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadixPrefixCache(0));
    }

    [Fact]
    public void Recruiting_agent_run_computes_5450_of_22750_prompt_tokens()
    {
        // Prompt blocks (each a distinct token range). Stable blocks first, the task line last.
        int[] sys = T.Range(0, 3000);                 // system prompt + tools + hiring policy
        int[] job = T.Range(10_000, 500);             // parsed job spec
        int[] rub = T.Range(20_000, 400);             // scoring rubric
        int[] Cat(params int[][] parts) => parts.SelectMany(p => p).ToArray();

        var cache = new RadixPrefixCache(9_000);
        T.Serve(cache, Cat(sys, T.Range(90_000, 50)));                  // another manager, earlier: SYS is warm

        Assert.Equal(3000, T.Serve(cache, Cat(sys, T.Range(30_000, 50))));               // parse
        Assert.Equal(3000, T.Serve(cache, Cat(sys, job, T.Range(40_000, 100))));         // rubric
        Assert.Equal(3500, T.Serve(cache, Cat(sys, job, rub, T.Range(50_000, 2000))));   // score

        // The two dossiers run in parallel: both pin SYS, JOB and RUB at the same time.
        var ana = cache.Acquire(Cat(sys, job, rub, T.Range(60_000, 1300)));
        var bo = cache.Acquire(Cat(sys, job, rub, T.Range(70_000, 1100)));
        Assert.Equal(3900, ana.ReusedLength);
        Assert.Equal(3900, bo.ReusedLength);
        cache.Insert(ana);
        cache.Insert(bo);
        ana.Dispose();
        bo.Dispose();

        // 5 calls: 22,750 prompt tokens, 17,300 reused, 5,450 computed (the earlier manager's call excluded).
        Assert.Equal(3050 + 22_750, cache.RequestedTokens);
        Assert.Equal(17_300, cache.ReusedTokens);
        Assert.Equal(8_500, cache.CachedTokens);

        // Later, a second manager hiring for a different job needs 700 new tokens; only 500 are free.
        // LRU evicts the oldest unpinned leaves: the earlier parse (50), this run's parse (50), the rubric task (100).
        Assert.Equal(3000, T.Serve(cache, Cat(sys, T.Range(80_000, 600), T.Range(40_000, 100))));
        Assert.Equal(9_000, cache.CachedTokens);
        Assert.Equal(3000, T.Serve(cache, Cat(sys, T.Range(90_000, 50))));               // earlier parse: gone, SYS kept
        cache.Audit();
    }

    [Fact]
    public void Chat_traffic_skips_most_of_the_prefill()
    {
        // 50 users, one shared 1,000-token system prompt, 5 turns each. Each turn resends the whole
        // conversation so far plus a new 20-token message.
        var cache = new RadixPrefixCache(100_000);
        var system = T.Range(0, 1000);
        var token = 10_000;
        var conversations = Enumerable.Range(0, 50).Select(_ => new List<int>(system)).ToList();
        for (int turn = 0; turn < 5; turn++)
            foreach (var convo in conversations)
            {
                convo.AddRange(T.Range(token, 20));
                token += 20;
                T.Serve(cache, convo.ToArray());
            }

        Assert.True(cache.ReuseRatio > 0.95, $"reuse was {cache.ReuseRatio:P1}");
        Assert.Equal(1000 + 50 * 5 * 20, cache.CachedTokens);   // the system prompt is stored once
    }
}
