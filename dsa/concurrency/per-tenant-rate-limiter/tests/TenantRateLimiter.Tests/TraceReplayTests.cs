namespace TenantRateLimiter.Tests;

// The CSV rows in this file are test data written for these tests, in the same format
// as the Azure trace. They are not rows from the real trace.
public class TraceReplayTests
{
    private const string Header = "TIMESTAMP,ContextTokens,GeneratedTokens\n";

    private static IReadOnlyList<TraceRow> Rows(string body) => AzureTrace.Parse(new StringReader(Header + body));

    [Fact]
    public void Parse_reads_every_row()
    {
        var rows = Rows("2023-11-16 18:00:01.5000000,1000,10\n2023-11-16 18:00:00.2500000,2000,20\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new TraceRow(new DateTime(2023, 11, 16, 18, 0, 1, 500), 1000, 10), rows[0]);
    }

    [Fact]
    public void Parse_rejects_bad_files()
    {
        Assert.Throws<FormatException>(() => AzureTrace.Parse(new StringReader("time,tokens\n")));
        Assert.Throws<FormatException>(() => AzureTrace.Parse(new StringReader(Header)));

        var error = Assert.Throws<FormatException>(() => Rows("2023-11-16 18:00:00.0000000,10,1\nnot a row\n"));
        Assert.Contains("Line 3", error.Message);
    }

    [Fact]
    public void Averages_are_per_minute_over_first_to_last_request()
    {
        // 3 requests, 1000 + 2000 + 3000 tokens, first to last is 30 seconds = half a minute.
        var rows = Rows(
            "2023-11-16 18:00:00.0000000,1000,1\n" +
            "2023-11-16 18:00:10.0000000,2000,1\n" +
            "2023-11-16 18:00:30.0000000,3000,1\n");

        Assert.Equal(6, TraceReplay.RequestsPerMinute(rows), 9);
        Assert.Equal(12_000, TraceReplay.InputTokensPerMinute(rows), 9);

        var limits = TraceReplay.TwiceTheAverage(rows, TimeSpan.FromMinutes(1));
        Assert.Equal(12, limits.RequestsPerMinute, 9);
        Assert.Equal(24_000, limits.InputTokensPerMinute, 9);
    }

    [Fact]
    public void Busiest_window_slides_rather_than_using_clock_seconds()
    {
        // 0.6 s and 1.4 s are in different clock seconds but only 0.8 s apart.
        var rows = Rows(
            "2023-11-16 18:00:00.6000000,1,1\n" +
            "2023-11-16 18:00:01.4000000,1,1\n" +
            "2023-11-16 18:00:03.0000000,1,1\n");

        Assert.Equal(2, TraceReplay.BusiestWindow(rows, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, TraceReplay.BusiestWindow(rows, TimeSpan.FromSeconds(0.5)));
        Assert.Equal(3, TraceReplay.BusiestWindow(rows, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Replay_counts_each_customer_separately()
    {
        // "burst": 4 requests in the same second. "steady": one every 10 seconds.
        var burst = Rows(string.Concat(Enumerable.Repeat("2023-11-16 18:00:05.0000000,100,1\n", 4)));
        var steady = Rows(
            "2023-11-16 18:00:00.0000000,100,1\n" +
            "2023-11-16 18:00:10.0000000,100,1\n" +
            "2023-11-16 18:00:20.0000000,100,1\n");

        // Same limits for both: 2 requests per minute, so a bucket of 2.
        var results = TraceReplay.Run(
            [new Customer("burst", burst), new Customer("steady", steady)],
            _ => new TenantLimits(2, 1_000_000));

        Assert.Equal((2, 2, 0), (results[0].Allowed, results[0].Limited, results[0].TooBig));
        // steady: allowed at 0 s and 10 s. By 20 s the bucket has only 20/30 of a request.
        Assert.Equal([Outcome.Allowed, Outcome.Allowed, Outcome.Limited], results[1].Outcomes);
    }

    [Fact]
    public void A_customer_gets_the_same_answers_alone_or_next_to_a_noisy_one()
    {
        var noisy = Rows(string.Concat(Enumerable.Repeat("2023-11-16 18:00:01.0000000,500,1\n", 50)));
        var quiet = Rows(
            "2023-11-16 18:00:00.0000000,100,1\n" +
            "2023-11-16 18:00:01.0000000,100,1\n" +
            "2023-11-16 18:00:02.0000000,100,1\n");
        Func<string, TenantLimits> limits = _ => new TenantLimits(3, 1000);

        var alone = TraceReplay.Run([new Customer("quiet", quiet)], limits);
        var together = TraceReplay.Run([new Customer("noisy", noisy), new Customer("quiet", quiet)], limits);

        Assert.Equal(alone[0].Outcomes, together[1].Outcomes);
        Assert.True(together[0].Limited > 0);
    }
}
