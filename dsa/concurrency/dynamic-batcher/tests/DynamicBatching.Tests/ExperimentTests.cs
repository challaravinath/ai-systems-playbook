using DynamicBatching.Experiments;

namespace DynamicBatching.Tests;

public class ExperimentTests
{
    private static long Ms(double ms) => (long)(ms * TimeSpan.TicksPerMillisecond);

    private static BatchCost Flat(double ms) => new("flat", [(1, ms), (2, ms)]);

    [Fact]
    public void The_cost_table_gives_NVIDIAs_rows_and_the_line_between_them()
    {
        var cost = BatchCost.BertBaseA100;

        Assert.Equal(0.63, cost.Ms(1));
        Assert.Equal(3.89, cost.Ms(32));
        Assert.Equal(15.21, cost.Ms(128));
        Assert.Equal(1.125, cost.Ms(6), 9);    // halfway between 4 (0.94) and 8 (1.31)
        Assert.Throws<ArgumentOutOfRangeException>(() => cost.Ms(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => cost.Ms(129));
    }

    [Fact]
    public void A_bad_cost_table_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new BatchCost("x", [(2, 1.0)]));             // no size 1
        Assert.Throws<ArgumentException>(() => new BatchCost("x", [(1, 1.0), (1, 2.0)]));   // size twice
        Assert.Throws<ArgumentException>(() => new BatchCost("x", [(1, 1.0), (2, 0.0)]));   // zero time
    }

    [Fact]
    public void Poisson_arrivals_are_sorted_repeatable_and_at_the_asked_rate()
    {
        var a = Arrivals.Poisson(1000, 100_000, seed: 7);

        Assert.Equal(a, Arrivals.Poisson(1000, 100_000, seed: 7));
        Assert.True(a.Zip(a.Skip(1)).All(p => p.First <= p.Second));
        double rate = a.Length / (a[^1] / (double)TimeSpan.TicksPerSecond);
        Assert.InRange(rate, 980, 1020);
    }

    [Fact]
    public void The_busiest_hour_is_found_and_its_times_start_at_zero()
    {
        var csv = new StringReader("""
            TIMESTAMP,ContextTokens,GeneratedTokens
            2024-05-10 00:59:59.500000+00:00,10,1
            2024-05-10 01:00:00.250000+00:00,10,1
            2024-05-10 01:00:00.000000+00:00,10,1
            2024-05-10 01:30:00.000001+00:00,10,1
            2024-05-10 02:00:00.000000+00:00,10,1
            """);

        var hour = Arrivals.BusiestHour(csv);

        Assert.Equal(new DateTime(2024, 5, 10, 1, 0, 0, DateTimeKind.Utc), hour.HourUtc);
        Assert.Equal(5, hour.TraceRequests);
        Assert.Equal(new[] { 0, Ms(250), TimeSpan.TicksPerMinute * 30 + 10 }, hour.Times);   // sorted, 1 tick = 100 ns
    }

    [Fact]
    public void A_trace_with_the_wrong_header_or_a_bad_row_is_refused()
    {
        Assert.Throws<FormatException>(() => Arrivals.BusiestHour(new StringReader("a,b,c\n")));
        Assert.Throws<FormatException>(() => Arrivals.BusiestHour(new StringReader(
            "TIMESTAMP,ContextTokens,GeneratedTokens\nnot a time,1,1\n")));
    }

    [Fact]
    public void Speeding_up_divides_the_times_and_the_busiest_window_slides()
    {
        long[] times = [0, Ms(400), Ms(900), Ms(1000), Ms(1200), Ms(2500)];

        Assert.Equal(new[] { 0, Ms(40), Ms(90), Ms(100), Ms(120), Ms(250) }, Arrivals.SpeedUp(times, 10));
        Assert.Equal(4, Arrivals.Busiest(times, TimeSpan.FromSeconds(1)));   // 0.4, 0.9, 1.0, 1.2
    }

    // Hand-worked: N = 2, T = 5 ms, every batch takes 1 ms. Arrivals at 0, 1 and 10 ms.
    // 0 and 1 fill a batch at 1 ms and finish at 2. 10 waits alone until 15 and finishes at 16.
    // Latencies 2, 1 and 6 ms.
    [Fact]
    public async Task A_hand_worked_run_closes_on_size_then_on_time()
    {
        var r = await Experiment.RunAsync(Flat(1), maxBatchSize: 2, TimeSpan.FromMilliseconds(5), [0, Ms(1), Ms(10)]);

        Assert.Equal(2, r.Batches);
        Assert.Equal(2, r.P50Ms, 9);
        Assert.Equal(6, r.MaxMs, 9);
    }

    // Hand-worked: N = 2, T = 0, every batch takes 4 ms. Arrivals at 0, 1, 2 and 3 ms.
    // 0 runs alone (0-4). 1, 2 and 3 queue up meanwhile, so 1 and 2 go together (4-8), then 3 (8-12).
    // A batch formed with no waiting at all, because the GPU was busy. Latencies 4, 7, 6, 9 ms.
    [Fact]
    public async Task A_busy_gpu_forms_batches_with_no_wait()
    {
        var r = await Experiment.RunAsync(Flat(4), maxBatchSize: 2, TimeSpan.Zero, [0, Ms(1), Ms(2), Ms(3)]);

        Assert.Equal(3, r.Batches);
        Assert.Equal(6, r.P50Ms, 9);
        Assert.Equal(9, r.MaxMs, 9);
    }

    // A request that arrives exactly at the deadline goes to the next batch.
    // N = 5, T = 5 ms, 1 ms per batch: 0 runs 5-6; 5 waits from 5 to its own deadline at 10, finishes at 11.
    [Fact]
    public async Task A_request_arriving_at_the_deadline_waits_for_the_next_batch()
    {
        var r = await Experiment.RunAsync(Flat(1), maxBatchSize: 5, TimeSpan.FromMilliseconds(5), [0, Ms(5)]);

        Assert.Equal(2, r.Batches);
        Assert.Equal(6, r.MaxMs, 9);
    }

    // The GPU finishes first, then new requests arrive, even at the same instant.
    // N = 5, T = 0, 4 ms per batch, arrivals at 0, 1 and 4 ms: 0 runs 0-4; at 4 the overdue 1 starts
    // alone (4-8) before 4 arrives, so 4 runs 8-12. Latencies 4, 7, 8 ms.
    [Fact]
    public async Task A_request_arriving_as_the_gpu_finishes_waits_for_the_batch_after()
    {
        var r = await Experiment.RunAsync(Flat(4), maxBatchSize: 5, TimeSpan.Zero, [0, Ms(1), Ms(4)]);

        Assert.Equal(3, r.Batches);
        Assert.Equal(8, r.MaxMs, 9);
    }

    [Fact]
    public async Task Under_overload_throughput_matches_the_cost_table()
    {
        var flood = Arrivals.Poisson(20_000, 5_000, seed: 3);

        var r = await Experiment.RunAsync(BatchCost.BertBaseA100, 8, TimeSpan.FromMilliseconds(5), flood);

        double expected = 8 / BatchCost.BertBaseA100.Ms(8) * 1000;   // 6,107 per second
        Assert.InRange(r.ThroughputPerSecond, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public async Task The_same_run_gives_the_same_numbers()
    {
        var arrivals = Arrivals.Poisson(3_000, 5_000, seed: 11);

        var first = await Experiment.RunAsync(BatchCost.BertBaseA100, 32, TimeSpan.FromMilliseconds(2), arrivals);
        var second = await Experiment.RunAsync(BatchCost.BertBaseA100, 32, TimeSpan.FromMilliseconds(2), arrivals);

        Assert.Equal(first, second);
    }

    // On the machine's real clock. Before the fix, timers that fired early closed about 3 in 10
    // of these batches before T (measured on Windows: 61 of 200 at T = 10 ms).
    [Fact]
    public async Task On_the_real_clock_no_batch_closes_before_T()
    {
        var row = await RealClockCheck.MeasureAsync(TimeSpan.FromMilliseconds(10), samples: 30, seed: 2);

        Assert.Equal(0, row.ClosedEarly);
        Assert.True(row.MinMs >= 10, $"shortest wait {row.MinMs} ms");
    }

    [Fact]
    public void Percentile_is_the_nearest_rank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(5, Experiment.Percentile(sorted, 50));
        Assert.Equal(10, Experiment.Percentile(sorted, 99));
        Assert.Equal(1, Experiment.Percentile(sorted, 0));
    }
}
