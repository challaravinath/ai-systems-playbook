using FewestGpus;

namespace FewestGpus.Tests;

// Every trace in this file is test data, made to exercise the code.
// None of it is a measurement of a real GPU or model.
public class ServingSimulatorTests
{
    private static Request[] Burst(int count, double prefillMs) =>
        Enumerable.Range(0, count).Select(_ => new Request(0, prefillMs)).ToArray();

    private static Request[] RandomTrace(Random random, int count)
    {
        double t = 0;
        var trace = new Request[count];
        for (int i = 0; i < count; i++)
        {
            t += random.Next(0, 4) == 0 ? 0 : random.NextDouble() * 50; // some exact ties on purpose
            trace[i] = new Request(t, 5 + (random.NextDouble() * 200));
        }
        // Shuffle so the simulator has to sort by arrival itself.
        return trace.OrderBy(_ => random.Next()).ToArray();
    }

    [Fact]
    public void Three_requests_at_once_on_one_replica_queue_behind_each_other()
    {
        Assert.Equal([100.0, 200.0, 300.0], ServingSimulator.TimeToFirstToken(Burst(3, 100), 1));
    }

    [Fact]
    public void Two_replicas_take_two_requests_at_once_and_the_third_waits()
    {
        Assert.Equal([100.0, 100.0, 200.0], ServingSimulator.TimeToFirstToken(Burst(3, 100), 2));
    }

    [Fact]
    public void A_request_that_arrives_after_the_replica_is_free_does_not_wait()
    {
        Request[] trace = [new(0, 100), new(150, 100)];

        Assert.Equal([100.0, 100.0], ServingSimulator.TimeToFirstToken(trace, 1));
    }

    [Fact]
    public void A_request_that_arrives_while_the_replica_is_busy_waits_only_for_the_rest()
    {
        Request[] trace = [new(0, 100), new(60, 100)];

        // Second request waits 40 ms, then needs 100 ms.
        Assert.Equal([100.0, 140.0], ServingSimulator.TimeToFirstToken(trace, 1));
    }

    [Fact]
    public void Results_come_back_in_the_order_requests_were_given_not_arrival_order()
    {
        Request[] trace = [new(60, 100), new(0, 100)];

        Assert.Equal([140.0, 100.0], ServingSimulator.TimeToFirstToken(trace, 1));
    }

    [Fact]
    public void Requests_arriving_at_the_same_time_are_served_in_the_order_given()
    {
        Request[] trace = [new(0, 10), new(0, 500)];

        Assert.Equal([10.0, 510.0], ServingSimulator.TimeToFirstToken(trace, 1));
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(0.5, 1)]
    public void Percentile_uses_nearest_rank(double p, double expected)
    {
        double[] values = Enumerable.Range(1, 100).Select(v => (double)v).Reverse().ToArray();

        Assert.Equal(expected, ServingSimulator.Percentile(values, p));
    }

    [Fact]
    public void P99_of_ten_values_is_the_largest()
    {
        // ceil(0.99 * 10) = 10, so with few samples p99 is the worst one.
        double[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 1000];

        Assert.Equal(1000, ServingSimulator.Percentile(values, 99));
    }

    [Fact]
    public void Adding_a_replica_never_raises_any_requests_time_to_first_token()
    {
        // The search is only correct if the check is monotone. This tests the stronger
        // property: every single request does at least as well with one more replica.
        var random = new Random(20261104);
        for (int trial = 0; trial < 300; trial++)
        {
            Request[] trace = RandomTrace(random, random.Next(1, 120));
            double[] previous = ServingSimulator.TimeToFirstToken(trace, 1);
            for (int n = 2; n <= 12; n++)
            {
                double[] current = ServingSimulator.TimeToFirstToken(trace, n);
                for (int i = 0; i < trace.Length; i++)
                {
                    Assert.True(current[i] <= previous[i] + 1e-9,
                        $"trial {trial}, request {i}: {previous[i]} with {n - 1} replicas, {current[i]} with {n}");
                }
                previous = current;
            }
        }
    }

    [Fact]
    public void Replicas_beyond_the_number_of_requests_change_nothing()
    {
        Request[] trace = [new(0, 30), new(5, 30), new(7, 30)];

        Assert.Equal(
            ServingSimulator.TimeToFirstToken(trace, 3),
            ServingSimulator.TimeToFirstToken(trace, 50));
    }

    [Fact]
    public async Task Fewest_replicas_for_a_burst_of_ten_with_a_250_ms_p99_target_is_five()
    {
        // 10 requests at once, 100 ms each. With 5 replicas the last pair finishes at 200 ms.
        // With 4, the slowest waits for two earlier rounds and finishes at 300 ms.
        Request[] burst = Burst(10, 100);
        var result = await ServingSimulator.FewestReplicasAsync(burst, ServingSimulator.MeetsTarget(burst, 99, 250));

        Assert.Equal(5, result.Answer);
    }

    [Fact]
    public async Task No_answer_when_one_request_alone_is_slower_than_the_target()
    {
        Request[] trace = [new(0, 50), new(10, 400)];

        var result = await ServingSimulator.FewestReplicasAsync(trace, ServingSimulator.MeetsTarget(trace, 100, 300));

        Assert.Null(result.Answer);
    }

    [Fact]
    public async Task Fewest_replicas_matches_trying_every_count_on_random_traces()
    {
        var random = new Random(4112026);
        for (int trial = 0; trial < 300; trial++)
        {
            Request[] trace = RandomTrace(random, random.Next(1, 80));
            double percentile = new[] { 50.0, 90.0, 99.0, 100.0 }[random.Next(4)];
            double target = 50 + (random.NextDouble() * 600);

            int? expected = null;
            for (int n = 1; n <= trace.Length; n++)
            {
                if (ServingSimulator.Percentile(ServingSimulator.TimeToFirstToken(trace, n), percentile) <= target)
                {
                    expected = n;
                    break;
                }
            }

            var result = await ServingSimulator.FewestReplicasAsync(trace, ServingSimulator.MeetsTarget(trace, percentile, target));

            Assert.Equal(expected, result.Answer);
        }
    }

    [Fact]
    public async Task The_check_keeps_its_own_copy_of_the_trace()
    {
        var trace = new List<Request>(Burst(4, 100));
        var check = ServingSimulator.MeetsTarget(trace, 100, 100);

        trace.Add(new Request(0, 100_000));

        Assert.True(await check(4, CancellationToken.None));
    }

    [Fact]
    public void Bad_inputs_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ServingSimulator.TimeToFirstToken([], 1));
        Assert.Throws<ArgumentException>(() => ServingSimulator.TimeToFirstToken([new(-1, 10)], 1));
        Assert.Throws<ArgumentException>(() => ServingSimulator.TimeToFirstToken([new(0, double.NaN)], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.TimeToFirstToken([new(0, 10)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.Percentile([1.0], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.Percentile([1.0], 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServingSimulator.MeetsTarget([new(0, 10)], 99, double.NaN));
    }
}
