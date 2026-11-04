using FewestGpus;

namespace FewestGpus.Tests;

// The CSV rows in this file are test data written for these tests, in the same format
// as the Azure trace. They are not rows from the real trace.
public class TraceAndPrefillTests
{
    private const string Sample =
        "TIMESTAMP,ContextTokens,GeneratedTokens\n" +
        "2023-11-16 18:00:01.5000000,1000,10\n" +
        "2023-11-16 18:00:00.2500000,2000,20\n" +
        "2023-11-16 18:00:01.0000000,500,5\n";

    // Baseline = each request's own prefill time, for tests where that is the point.
    private static double[] Idle(Request[] trace) => trace.Select(r => r.PrefillMs).ToArray();

    [Fact]
    public void Line_through_two_points()
    {
        var line = PrefillModel.FromTwoPoints("test", 1000, 100, 2000, 150);

        Assert.Equal(50, line.FixedMs, 9);
        Assert.Equal(0.05, line.MsPerToken, 9);
        Assert.Equal(75, line.IdleTtftMs(500), 9);
    }

    [Fact]
    public void Splitwise_lines_give_back_the_table_medians()
    {
        // Table IV: coding A100 185 ms, H100 95 ms; conversation A100 155 ms, H100 84 ms.
        Assert.Equal(185, PrefillModel.SplitwiseA100.IdleTtftMs(PrefillModel.CodingMedianTokens), 9);
        Assert.Equal(155, PrefillModel.SplitwiseA100.IdleTtftMs(PrefillModel.ConversationMedianTokens), 9);
        Assert.Equal(95, PrefillModel.SplitwiseH100.IdleTtftMs(PrefillModel.CodingMedianTokens), 9);
        Assert.Equal(84, PrefillModel.SplitwiseH100.IdleTtftMs(PrefillModel.ConversationMedianTokens), 9);
    }

    [Fact]
    public void Splitwise_A100_line_is_the_paper_arithmetic()
    {
        // Per token: (185 - 155) / (1469 - 1020) = 30 / 449. Fixed: 155 - 1020 * 30 / 449.
        Assert.Equal(30.0 / 449, PrefillModel.SplitwiseA100.MsPerToken, 12);
        Assert.Equal(155 - (1020 * 30.0 / 449), PrefillModel.SplitwiseA100.FixedMs, 9);
    }

    [Fact]
    public void Lines_that_cannot_be_prefill_times_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => PrefillModel.FromTwoPoints("same size", 1000, 100, 1000, 150));
        Assert.Throws<ArgumentException>(() => PrefillModel.FromTwoPoints("falls", 1000, 200, 2000, 100));
    }

    [Fact]
    public void Parse_reads_every_row()
    {
        var rows = AzureTrace.Parse(new StringReader(Sample));

        Assert.Equal(3, rows.Count);
        Assert.Equal(new TraceRow(new DateTime(2023, 11, 16, 18, 0, 1, 500), 1000, 10), rows[0]);
    }

    [Fact]
    public void Parse_rejects_a_file_with_the_wrong_header()
    {
        Assert.Throws<FormatException>(() => AzureTrace.Parse(new StringReader("time,tokens\n2023-11-16 18:00:00.0,1\n")));
    }

    [Fact]
    public void Parse_names_the_line_of_a_bad_row()
    {
        string bad = "TIMESTAMP,ContextTokens,GeneratedTokens\n2023-11-16 18:00:00.0000000,10,1\nnot a row\n";

        var error = Assert.Throws<FormatException>(() => AzureTrace.Parse(new StringReader(bad)));
        Assert.Contains("Line 3", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_trace_with_no_rows()
    {
        Assert.Throws<FormatException>(() => AzureTrace.Parse(new StringReader("TIMESTAMP,ContextTokens,GeneratedTokens\n")));
    }

    [Fact]
    public void Arrivals_are_milliseconds_after_the_earliest_row_whatever_the_row_order()
    {
        var rows = AzureTrace.Parse(new StringReader(Sample));
        var line = PrefillModel.FromTwoPoints("test", 1000, 100, 2000, 150);

        Request[] requests = AzureTrace.ToRequests(rows, line);

        Assert.Equal([1250.0, 0.0, 750.0], requests.Select(r => r.ArrivalMs));
        Assert.Equal([100.0, 150.0, 75.0], requests.Select(r => r.PrefillMs));
    }

    [Fact]
    public void Busiest_second_counts_requests_per_calendar_second()
    {
        var rows = AzureTrace.Parse(new StringReader(Sample));

        var busiest = AzureTrace.BusiestSecond(rows);

        Assert.Equal(new DateTime(2023, 11, 16, 18, 0, 1), busiest.Second);
        Assert.Equal(2, busiest.Requests);
    }

    [Fact]
    public async Task Slowdown_is_ttft_over_the_idle_ttft()
    {
        // Two requests at once, 100 ms each, one replica: TTFT 100 and 200, slowdown 1 and 2.
        Request[] trace = [new(0, 100), new(0, 100)];

        Assert.True(await ServingSimulator.MeetsSlowdownTarget(trace, Idle(trace), 100, 2)(1, CancellationToken.None));
        Assert.False(await ServingSimulator.MeetsSlowdownTarget(trace, Idle(trace), 100, 1.9)(1, CancellationToken.None));
        Assert.True(await ServingSimulator.MeetsSlowdownTarget(trace, Idle(trace), 100, 1)(2, CancellationToken.None));
    }

    [Fact]
    public async Task A_short_prompt_stuck_behind_a_long_one_has_a_big_slowdown()
    {
        // The short request waits 1000 ms for the long one, then needs 50 ms: slowdown 21.
        Request[] trace = [new(0, 1000), new(0, 50)];

        Assert.False(await ServingSimulator.MeetsSlowdownTarget(trace, Idle(trace), 100, 6)(1, CancellationToken.None));
        Assert.True(await ServingSimulator.MeetsSlowdownTarget(trace, Idle(trace), 100, 21)(1, CancellationToken.None));
    }

    [Fact]
    public async Task Slowdown_check_only_turns_from_fail_to_pass_as_replicas_are_added()
    {
        var random = new Random(20231116);
        for (int trial = 0; trial < 200; trial++)
        {
            var trace = new Request[random.Next(1, 100)];
            double t = 0;
            for (int i = 0; i < trace.Length; i++)
            {
                t += random.Next(3) == 0 ? 0 : random.NextDouble() * 100;
                trace[i] = new Request(t, 20 + (random.NextDouble() * 500));
            }
            double limit = 1 + (random.NextDouble() * 8);
            var check = ServingSimulator.MeetsSlowdownTarget(trace, Idle(trace), 99, limit);

            bool passedBefore = false;
            for (int n = 1; n <= 15; n++)
            {
                bool passes = await check(n, CancellationToken.None);
                Assert.False(passedBefore && !passes, $"trial {trial}: passed with {n - 1} replicas, failed with {n}");
                passedBefore |= passes;
            }
        }
    }

    [Fact]
    public async Task Slowdown_is_measured_against_the_baseline_not_the_machine_being_sized()
    {
        // A faster machine (50 ms) judged against a slower reference (100 ms): two at once on
        // one machine take 50 and 100 ms, so slowdowns 0.5 and 1.0 against the reference.
        Request[] fast = [new(0, 50), new(0, 50)];
        double[] reference = [100, 100];

        Assert.Equal(1.0, ServingSimulator.SlowdownPercentile(fast, reference, 1, 100), 9);
        Assert.True(await ServingSimulator.MeetsSlowdownTarget(fast, reference, 100, 1)(1, CancellationToken.None));
    }

    [Fact]
    public void Slowdown_needs_one_baseline_per_request()
    {
        Assert.Throws<ArgumentException>(() => ServingSimulator.MeetsSlowdownTarget([new(0, 10), new(0, 10)], [10.0], 99, 6));
    }

    [Fact]
    public async Task Fewest_replicas_doubles_from_one()
    {
        Request[] trace = Enumerable.Range(0, 10).Select(_ => new Request(0, 100)).ToArray();

        var result = await ServingSimulator.FewestReplicasAsync(trace, ServingSimulator.MeetsTarget(trace, 99, 250));

        Assert.Equal(5, result.Answer);
        Assert.Equal([1, 2, 4, 8, 6, 5], result.Log.Select(c => c.Candidate));
    }

    [Fact]
    public void Slowdown_check_rejects_inputs_it_cannot_use()
    {
        Assert.Throws<ArgumentException>(() => ServingSimulator.MeetsSlowdownTarget([new(0, 10)], [0.0], 99, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.MeetsSlowdownTarget([new(0, 10)], [10.0], 99, 0.5));
    }
}
