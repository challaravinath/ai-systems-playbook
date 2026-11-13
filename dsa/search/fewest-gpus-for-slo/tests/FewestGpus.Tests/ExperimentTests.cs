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
}
