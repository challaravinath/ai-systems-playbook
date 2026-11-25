namespace DynamicBatching.Tests;

// The oracle is a plain loop over the arrival list, written separately from the batcher.
// For random arrivals, the real batcher on a fake clock must close the same batches at the same times.
public class OracleTests
{
    // Arrivals sorted by time. A batch opens with the oldest waiting request and closes when it
    // has n requests, or at open + maxWait. A request arriving exactly at the deadline goes into the
    // next batch, because the clock reaches the deadline before that request is sent.
    // The batch function takes no time here, so the loop is never busy.
    internal static List<(long AtMs, int[] Items)> Expected(IReadOnlyList<(long T, int Id)> arrivals, int n, long maxWaitMs)
    {
        var batches = new List<(long, int[])>();
        int i = 0;
        while (i < arrivals.Count)
        {
            long deadline = arrivals[i].T + maxWaitMs;
            var items = new List<int> { arrivals[i].Id };
            long closeAt = items.Count == n ? arrivals[i].T : deadline;
            i++;
            while (items.Count < n && i < arrivals.Count && arrivals[i].T < deadline)
            {
                items.Add(arrivals[i].Id);
                if (items.Count == n)
                {
                    closeAt = arrivals[i].T;
                }
                i++;
            }
            batches.Add((closeAt, items.ToArray()));
        }
        return batches;
    }

    [Fact]
    public void The_oracle_matches_a_hand_worked_example()
    {
        // n = 3, wait 10 ms. Requests 1, 2, 3 arrive at 0, 2, 4 ms and fill a batch at 4.
        // Request 4 arrives at 5 and opens the next batch (deadline 15). Request 5 arrives at 15,
        // too late for it, so request 4 runs alone at 15. Request 5 opens its own batch and runs at 25.
        var arrivals = new List<(long, int)> { (0, 1), (2, 2), (4, 3), (5, 4), (15, 5) };

        var expected = Expected(arrivals, n: 3, maxWaitMs: 10);

        Assert.Equal("4:1,2,3 | 15:4 | 25:5",
            string.Join(" | ", expected.Select(b => $"{b.AtMs}:{string.Join(",", b.Items)}")));
    }

    // 20 seeds x 50 random patterns. Raising this to 400 seeds once found a race in the test helper.
    public static IEnumerable<object[]> Seeds => Enumerable.Range(1, 20).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task The_batcher_matches_the_oracle_on_random_arrivals(int seed)
    {
        var rng = new Random(seed);
        for (int trial = 0; trial < 50; trial++)
        {
            int n = rng.Next(1, 6);
            int maxWaitMs = rng.Next(0, 21);
            int count = rng.Next(1, 30);
            var arrivals = Enumerable.Range(1, count)
                .Select(id => ((long)rng.Next(0, 200), id))
                .OrderBy(a => a.Item1).ThenBy(a => a.id)
                .ToList();

            await using var h = new Harness(n, maxWaitMs);
            var tasks = new List<Task<int>>();
            foreach (var (t, id) in arrivals)
            {
                h.AdvanceMs((int)(t - h.NowMs));
                tasks.Add(h.Submit(id));
            }
            h.AdvanceMs(maxWaitMs + 1);
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

            var expected = Expected(arrivals, n, maxWaitMs);
            var actual = h.Batches;
            string where = $"seed {seed}, trial {trial}, n {n}, wait {maxWaitMs}";
            Assert.True(expected.Count == actual.Count, $"{where}: {actual.Count} batches, expected {expected.Count}");
            for (int b = 0; b < expected.Count; b++)
            {
                Assert.True(expected[b].AtMs == actual[b].AtMs, $"{where}: batch {b} closed at {actual[b].AtMs}, expected {expected[b].AtMs}");
                Assert.True(expected[b].Items.SequenceEqual(actual[b].Items), $"{where}: batch {b} items differ");
            }
        }
    }
}
