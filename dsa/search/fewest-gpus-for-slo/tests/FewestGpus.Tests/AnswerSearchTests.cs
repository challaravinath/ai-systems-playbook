using FewestGpus;

namespace FewestGpus.Tests;

public class AnswerSearchTests
{
    // A monotone check with a known threshold: passes for every n >= threshold.
    // threshold = null means nothing passes. This is test data, not a model of GPUs.
    private static Func<int, CancellationToken, ValueTask<bool>> Threshold(int? threshold, List<int>? calls = null) =>
        (n, _) =>
        {
            calls?.Add(n);
            return ValueTask.FromResult(threshold is int t && n >= t);
        };

    private static int MaxChecks(long rangeSize) => (int)Math.Floor(Math.Log2(rangeSize)) + 1;

    [Fact]
    public async Task Paper_row_1_to_16_with_answer_11_takes_four_checks()
    {
        var result = await AnswerSearch.FewestThatPassAsync(1, 16, Threshold(11));

        Assert.Equal(11, result.Answer);
        Assert.Equal(
            [new Check(8, false), new Check(12, true), new Check(10, false), new Check(11, true)],
            result.Log);
    }

    [Fact]
    public async Task Nothing_passing_returns_no_answer()
    {
        var result = await AnswerSearch.FewestThatPassAsync(1, 16, Threshold(null));

        Assert.Null(result.Answer);
        Assert.All(result.Log, c => Assert.False(c.Passed));
    }

    [Fact]
    public async Task Everything_passing_returns_the_start_of_the_range()
    {
        var result = await AnswerSearch.FewestThatPassAsync(3, 40, Threshold(0));

        Assert.Equal(3, result.Answer);
    }

    [Fact]
    public async Task Single_candidate_range_makes_exactly_one_check()
    {
        var pass = await AnswerSearch.FewestThatPassAsync(5, 5, Threshold(5));
        var fail = await AnswerSearch.FewestThatPassAsync(5, 5, Threshold(6));

        Assert.Equal(5, pass.Answer);
        Assert.Null(fail.Answer);
        Assert.Equal(1, pass.Checks);
        Assert.Equal(1, fail.Checks);
    }

    [Fact]
    public async Task The_answer_passed_and_the_one_below_it_failed_both_seen_in_the_log()
    {
        // The result is backed by evidence: the answer was checked and passed,
        // and unless it is the start of the range, answer - 1 was checked and failed.
        var result = await AnswerSearch.FewestThatPassAsync(1, 1000, Threshold(377));

        Assert.Contains(new Check(377, true), result.Log);
        Assert.Contains(new Check(376, false), result.Log);
    }

    [Fact]
    public async Task Matches_a_linear_scan_on_random_ranges_and_thresholds()
    {
        var random = new Random(20261104);
        for (int trial = 0; trial < 2000; trial++)
        {
            int lo = random.Next(0, 50);
            int hi = lo + random.Next(0, 300);
            // Thresholds below, inside and above the range, plus "nothing passes".
            int? threshold = random.Next(10) == 0 ? null : random.Next(lo - 5 < 0 ? 0 : lo - 5, hi + 6);

            int? expected = null;
            for (int n = lo; n <= hi; n++)
            {
                if (threshold is int t && n >= t)
                {
                    expected = n;
                    break;
                }
            }

            var calls = new List<int>();
            var result = await AnswerSearch.FewestThatPassAsync(lo, hi, Threshold(threshold, calls));

            Assert.Equal(expected, result.Answer);
            Assert.True(result.Checks <= MaxChecks(hi - lo + 1), $"{result.Checks} checks for range {lo}..{hi}");
            Assert.Equal(calls.Count, calls.Distinct().Count());
            Assert.All(calls, n => Assert.InRange(n, lo, hi));
        }
    }

    [Fact]
    public async Task Range_near_int_max_does_not_overflow()
    {
        int hi = int.MaxValue - 1;
        var result = await AnswerSearch.FewestThatPassAsync(0, hi, Threshold(hi));

        Assert.Equal(hi, result.Answer);
        Assert.True(result.Checks <= 32);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(6, 5)]
    [InlineData(0, int.MaxValue)]
    public async Task Bad_ranges_are_rejected(int lo, int hi)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => AnswerSearch.FewestThatPassAsync(lo, hi, Threshold(1)));
    }

    [Fact]
    public async Task Cancelling_stops_the_search_before_the_next_check()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<int>();
        Func<int, CancellationToken, ValueTask<bool>> check = (n, _) =>
        {
            calls.Add(n);
            cts.Cancel(); // cancel while the first check is running
            return ValueTask.FromResult(false);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AnswerSearch.FewestThatPassAsync(1, 1000, check, cts.Token));
        Assert.Single(calls);
    }

    [Fact]
    public async Task Unbounded_doubles_then_narrows()
    {
        var result = await AnswerSearch.FewestThatPassUnboundedAsync(1, 1_000_000, Threshold(11));

        Assert.Equal(11, result.Answer);
        Assert.Equal(
            [
                new Check(1, false), new Check(2, false), new Check(4, false), new Check(8, false),
                new Check(16, true),
                new Check(12, true), new Check(10, false), new Check(11, true),
            ],
            result.Log);
    }

    [Fact]
    public async Task Unbounded_cost_depends_on_the_answer_not_the_limit()
    {
        var small = await AnswerSearch.FewestThatPassUnboundedAsync(1, 1_000, Threshold(11));
        var huge = await AnswerSearch.FewestThatPassUnboundedAsync(1, int.MaxValue - 1, Threshold(11));

        Assert.Equal(small.Log, huge.Log);
    }

    [Fact]
    public async Task Unbounded_never_goes_above_the_limit_and_reports_no_answer()
    {
        var calls = new List<int>();
        var result = await AnswerSearch.FewestThatPassUnboundedAsync(1, 100, Threshold(null, calls));

        Assert.Null(result.Answer);
        Assert.Equal([1, 2, 4, 8, 16, 32, 64, 100], calls);
    }

    [Fact]
    public async Task Unbounded_answer_at_the_limit_is_found()
    {
        var result = await AnswerSearch.FewestThatPassUnboundedAsync(1, 100, Threshold(100));

        Assert.Equal(100, result.Answer);
    }

    [Fact]
    public async Task Unbounded_matches_a_linear_scan_on_random_cases()
    {
        var random = new Random(4112026);
        for (int trial = 0; trial < 2000; trial++)
        {
            int start = random.Next(1, 20);
            int limit = start + random.Next(0, 5000);
            int? threshold = random.Next(10) == 0 ? null : random.Next(0, limit + 10);

            int? expected = null;
            for (int n = start; n <= limit; n++)
            {
                if (threshold is int t && n >= t)
                {
                    expected = n;
                    break;
                }
            }

            var calls = new List<int>();
            var result = await AnswerSearch.FewestThatPassUnboundedAsync(start, limit, Threshold(threshold, calls));

            Assert.Equal(expected, result.Answer);
            Assert.Equal(calls.Count, calls.Distinct().Count());
            Assert.All(calls, n => Assert.InRange(n, start, limit));

            if (expected is int a)
            {
                // Doubling: ceil(log2(a / start)) + 1 checks at most.
                // Narrowing: a range smaller than a, so floor(log2(a)) + 1 checks at most.
                int doubling = (int)Math.Ceiling(Math.Log2(Math.Max(1.0, (double)a / start))) + 1;
                Assert.True(result.Checks <= doubling + MaxChecks(a), $"{result.Checks} checks for answer {a}, start {start}");
            }
        }
    }

    [Fact]
    public async Task Unbounded_rejects_a_start_of_zero()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => AnswerSearch.FewestThatPassUnboundedAsync(0, 10, Threshold(1)));
    }
}
