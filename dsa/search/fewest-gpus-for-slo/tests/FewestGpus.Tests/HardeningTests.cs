using FewestGpus;

namespace FewestGpus.Tests;

// Each test here protects a guard that a deliberately broken copy of the code got past.
// The traces are test data, not measurements of a real GPU or model.
public class HardeningTests
{
    [Fact]
    public async Task Doubling_near_int_max_never_calls_the_check_outside_the_range()
    {
        var calls = new List<int>();
        int limit = int.MaxValue - 1;

        var result = await AnswerSearch.FewestThatPassUnboundedAsync(1, limit, (n, _) =>
        {
            calls.Add(n);
            return ValueTask.FromResult(false);
        });

        Assert.Null(result.Answer);
        Assert.All(calls, n => Assert.InRange(n, 1, limit));
        Assert.Equal(limit, calls[^1]);
    }

    [Fact]
    public async Task Doubling_stops_at_the_next_step_once_cancelled()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AnswerSearch.FewestThatPassUnboundedAsync(1, 1000, (n, _) =>
            {
                calls++;
                cts.Cancel();
                return ValueTask.FromResult(false);
            }, cts.Token));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(90, 15, 14)]   // 13.5 rounds up to 14
    [InlineData(7, 100, 7)]    // 7 / 100.0 * 100 is a hair above 7 in double
    [InlineData(55, 100, 55)]
    [InlineData(99.9, 33_000, 32_967)]
    public void Percentile_rank_is_the_exact_ceiling(double p, int n, int expectedRank)
    {
        double[] values = Enumerable.Range(1, n).Select(i => (double)i).ToArray();

        Assert.Equal(expectedRank, ServingSimulator.Percentile(values, p));
    }

    [Fact]
    public void A_huge_machine_count_gives_the_same_answer_as_one_machine_per_request()
    {
        Request[] trace = [new(0, 100), new(0, 100), new(10, 50)];

        Assert.Equal(ServingSimulator.TimeToFirstToken(trace, 3), ServingSimulator.TimeToFirstToken(trace, int.MaxValue - 1));
    }

    [Fact]
    public void Bad_requests_and_targets_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ServingSimulator.TimeToFirstToken([new(0, -1)], 1));
        Assert.Throws<ArgumentException>(() => ServingSimulator.TimeToFirstToken([new(double.NaN, 10)], 1));
        Assert.Throws<ArgumentException>(() => ServingSimulator.TimeToFirstToken([new(double.PositiveInfinity, 10)], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.MeetsTarget([new(0, 10)], 99, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => ServingSimulator.MeetsSlowdownTarget([new(0, 10)], [double.PositiveInfinity], 99, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.MeetsSlowdownTarget([new(0, 10)], [10.0], 99, double.NaN));
    }

    [Fact]
    public void A_line_with_a_negative_fixed_part_is_rejected()
    {
        // Rises 0.2 ms per token, but would be -10 ms at 0 tokens.
        Assert.Throws<ArgumentException>(() => PrefillModel.FromTwoPoints("x", 100, 10, 200, 30));
    }

    [Fact]
    public void A_row_with_an_extra_field_is_rejected()
    {
        var csv = "TIMESTAMP,ContextTokens,GeneratedTokens\n2023-11-16 18:00:00.0000000,10,1,9\n";

        Assert.Throws<FormatException>(() => AzureTrace.Parse(new StringReader(csv)));
    }

    [Fact]
    public async Task The_slowdown_check_keeps_its_own_copies()
    {
        var trace = new List<Request> { new(0, 100), new(0, 100) };
        var baseline = new List<double> { 100, 100 };
        var check = ServingSimulator.MeetsSlowdownTarget(trace, baseline, 100, 1);

        trace[1] = new Request(0, 100_000);
        baseline[1] = 0.001;

        Assert.True(await check(2, CancellationToken.None));
    }

    [Fact]
    public void Slowdown_percentile_needs_exactly_one_baseline_per_request()
    {
        Request[] trace = [new(0, 10), new(0, 10)];

        Assert.Throws<ArgumentException>(() => ServingSimulator.SlowdownPercentile(trace, [10.0], 1, 99));
        Assert.Throws<ArgumentException>(() => ServingSimulator.SlowdownPercentile(trace, [10.0, 10.0, 10.0], 1, 99));
    }

    [Fact]
    public void Busiest_second_ties_go_to_the_earliest_second()
    {
        var csv = "TIMESTAMP,ContextTokens,GeneratedTokens\n" +
                  "2023-11-16 18:00:05.1000000,1,1\n2023-11-16 18:00:05.2000000,1,1\n" +
                  "2023-11-16 18:00:02.1000000,1,1\n2023-11-16 18:00:02.2000000,1,1\n";

        var (second, requests) = AzureTrace.BusiestSecond(AzureTrace.Parse(new StringReader(csv)));

        Assert.Equal(2, requests);
        Assert.Equal(new DateTime(2023, 11, 16, 18, 0, 2), second);
    }
}
