using FewestGpus;

namespace FewestGpus.Tests;

// Every trace here is test data, made to exercise the code. None of it is a GPU measurement.
public class ExperimentTests
{
    private static Request[] Steady(int count, double gapMs, double prefillMs) =>
        Enumerable.Range(0, count).Select(i => new Request(i * gapMs, prefillMs)).ToArray();

    [Fact]
    public void Poisson_keeps_the_prompts_the_count_and_the_average_rate()
    {
        var trace = Steady(20_000, 10, 7);

        var poisson = Arrivals.Poisson(trace, seed: 1);

        Assert.Equal(trace.Length, poisson.Length);
        Assert.All(poisson, r => Assert.Equal(7, r.PrefillMs));
        Assert.True(poisson.Zip(poisson.Skip(1)).All(p => p.First.ArrivalMs <= p.Second.ArrivalMs));
        double meanGap = poisson[^1].ArrivalMs / (poisson.Length - 1);
        Assert.InRange(meanGap, 9.7, 10.3);
        Assert.Equal(poisson, Arrivals.Poisson(trace, seed: 1));   // same seed, same arrivals
    }

    [Fact]
    public void Peak_machines_is_the_busiest_window_of_work()
    {
        // Three 100 ms requests in the first 10 ms, then one alone much later.
        Request[] trace = [new(0, 100), new(5, 100), new(9, 100), new(5_000, 100)];

        // In any 1-second window the most work is 300 ms, so 0.3 machines just to keep up.
        Assert.Equal(0.3, Arrivals.PeakMachines(trace, 1000), 9);
        // In a 10 ms window the first three still fit: 300 ms of work in 10 ms, 30 machines.
        Assert.Equal(30, Arrivals.PeakMachines(trace, 10), 9);
    }

    [Fact]
    public async Task A_noisy_check_with_no_noise_agrees_with_the_plain_check()
    {
        var random = new Random(5);
        for (int trial = 0; trial < 50; trial++)
        {
            Request[] trace = Enumerable.Range(0, 40)
                .Select(i => new Request(random.NextDouble() * 500, 10 + (random.NextDouble() * 90)))
                .ToArray();
            double[] baseline = trace.Select(r => r.PrefillMs).ToArray();
            var plain = ServingSimulator.MeetsSlowdownTarget(trace, baseline, 99, 3);
            var noisy = Checks.NoisySlowdownTarget(trace, baseline, 99, 3, sigma: 0, new Random(trial));

            for (int n = 1; n <= 12; n++)
            {
                Assert.Equal(await plain(n, CancellationToken.None), await noisy(n, CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task A_noisy_check_can_flip_on_the_same_machine_count()
    {
        // One request, 100 ms idle, and a target of exactly its idle time. With noise, its time
        // lands above or below 100 ms on each run, so the same machine count passes, then fails.
        Request[] trace = [new(0, 100)];
        double[] baseline = [100.0];
        var noisy = Checks.NoisySlowdownTarget(trace, baseline, 100, 1, sigma: 0.2, new Random(3));

        var answers = new List<bool>();
        for (int i = 0; i < 40; i++)
        {
            answers.Add(await noisy(1, CancellationToken.None));
        }

        Assert.Contains(true, answers);
        Assert.Contains(false, answers);
    }

    [Fact]
    public async Task Majority_of_three_passes_on_two_passes_and_runs_the_check_three_times()
    {
        var script = new Queue<bool>([true, false, true, false, false, true]);
        int calls = 0;
        var check = Checks.MajorityOf(3, (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(script.Dequeue());
        });

        Assert.True(await check(1, CancellationToken.None));    // Y N Y
        Assert.False(await check(1, CancellationToken.None));   // N N Y
        Assert.Equal(6, calls);
    }

    [Fact]
    public void Majority_needs_an_odd_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Checks.MajorityOf(2, (_, _) => ValueTask.FromResult(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Checks.MajorityOf(0, (_, _) => ValueTask.FromResult(true)));
    }

    [Fact]
    public async Task A_bad_minute_fails_the_window_check_but_hides_in_the_hourly_one()
    {
        // 99 quiet requests spread over 99 minutes, then 3 at once in minute 100. Each takes 100 ms idle.
        var trace = Enumerable.Range(0, 99).Select(i => new Request(i * 60_000, 100))
            .Concat(Enumerable.Repeat(new Request(99 * 60_000, 100), 3)).ToArray();
        double[] baseline = Enumerable.Repeat(100.0, trace.Length).ToArray();

        // On 1 machine the last 3 wait: slowdowns 1, 2, 3 in that minute.
        var hourly = ServingSimulator.MeetsSlowdownTarget(trace, baseline, 95, 2);
        var perMinute = Checks.SlowdownTargetInEveryWindow(trace, baseline, 95, 2, 60_000);

        Assert.True(await hourly(1, CancellationToken.None));
        Assert.False(await perMinute(1, CancellationToken.None));
        Assert.Equal(3, Checks.WorstWindowSlowdown(trace, baseline, 1, 95, 60_000), 9);
    }

    [Fact]
    public async Task The_window_check_only_turns_from_fail_to_pass_as_machines_are_added()
    {
        var random = new Random(9);
        for (int trial = 0; trial < 30; trial++)
        {
            Request[] trace = Enumerable.Range(0, 60)
                .Select(_ => new Request(random.NextDouble() * 10_000, 20 + (random.NextDouble() * 400)))
                .ToArray();
            double[] baseline = trace.Select(r => r.PrefillMs).ToArray();
            var check = Checks.SlowdownTargetInEveryWindow(trace, baseline, 90, 2, 2_000);

            bool seenPass = false;
            for (int n = 1; n <= 20; n++)
            {
                bool ok = await check(n, CancellationToken.None);
                Assert.False(seenPass && !ok, $"trial {trial}: passed below {n} machines, then failed at {n}");
                seenPass |= ok;
            }
        }
    }

    [Fact]
    public void A_block_bootstrap_keeps_whole_blocks_and_the_length()
    {
        // Three 1-second blocks: 1 request, 2 requests, 3 requests.
        Request[] trace = [new(0, 10), new(1000, 20), new(1500, 20), new(2000, 30), new(2100, 30), new(2200, 30)];

        var sample = Arrivals.BlockBootstrap(trace, 1000, seed: 4);

        Assert.All(sample, r => Assert.InRange(r.ArrivalMs, 0, 2999));
        // Every block that appears comes whole: its requests all share one prefill size here.
        foreach (var block in sample.GroupBy(r => Math.Floor(r.ArrivalMs / 1000)))
        {
            double size = block.First().PrefillMs;
            Assert.Equal((int)(size / 10), block.Count());
        }
        Assert.Equal(sample, Arrivals.BlockBootstrap(trace, 1000, seed: 4));
    }

    [Fact]
    public void Busiest_window_count_slides()
    {
        // 0.6 s and 1.4 s are in different clock seconds but only 0.8 s apart.
        Request[] trace = [new(600, 1), new(1400, 1), new(3000, 1)];

        Assert.Equal(2, Arrivals.BusiestWindowCount(trace, 1000));
        Assert.Equal(1, Arrivals.BusiestWindowCount(trace, 500));
    }

    [Fact]
    public async Task Run_level_noise_moves_the_whole_run_together()
    {
        // With only run-level noise, every request in one run gets the same factor,
        // so a burst of equal requests keeps equal slowdowns within a run.
        Request[] trace = [new(0, 100), new(0, 100)];
        double[] baseline = [100, 100];
        var noisy = Checks.NoisySlowdownTarget(trace, baseline, 100, 1.5, sigma: 0, new Random(2), runSigma: 0.3);

        var answers = new List<bool>();
        for (int i = 0; i < 60; i++)
        {
            answers.Add(await noisy(2, CancellationToken.None));
        }

        Assert.Contains(true, answers);
        Assert.Contains(false, answers);
    }

    [Fact]
    public async Task Confirming_the_boundary_moves_a_lucky_answer_back_up()
    {
        // The true answer is 11, but the search reported 10 after one lucky pass.
        // Re-checked as a majority, 10 fails, so the answer moves up to 11 and stays there.
        var script = new Dictionary<int, Queue<bool>>
        {
            [10] = new([false, true, false, false, false, true]),   // checked twice: as the answer, then as the one below 11
            [11] = new([true, true, true]),
            
        };
        var check = (Func<int, CancellationToken, ValueTask<bool>>)((n, _) => ValueTask.FromResult(script[n].Dequeue()));

        Assert.Equal(11, await Checks.ConfirmBoundaryAsync(10, 3, check, 100));
    }

    [Fact]
    public void A_failing_test_is_decided_at_the_miss_that_tips_it_over()
    {
        // Five requests at once, 100 ms each, on 1 machine: first tokens at 100 to 500 ms,
        // slowdowns 1 to 5. Target p80 at most 2 allows one miss. Misses at 300, 400 and 500 ms,
        // so the second miss, at 400 ms, decides it.
        Request[] trace = Enumerable.Range(0, 5).Select(_ => new Request(0, 100)).ToArray();
        double[] baseline = Enumerable.Repeat(100.0, 5).ToArray();

        Assert.Equal(400, Checks.MsUntilDecided(trace, baseline, 1, 80, 2), 9);
        // With 5 machines it passes, and a pass is only known at the end.
        Assert.Equal(100, Checks.MsUntilDecided(trace, baseline, 5, 80, 2), 9);
    }
}
