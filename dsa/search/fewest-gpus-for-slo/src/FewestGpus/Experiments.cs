namespace FewestGpus;

// Other shapes of the same traffic, for asking "what if".
public static class Arrivals
{
    // The same requests in the same order, but arriving as a Poisson process at the trace's
    // average rate: gaps drawn from an exponential distribution. It keeps every prompt size
    // and removes the bursts.
    public static Request[] Poisson(IReadOnlyList<Request> trace, int seed)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.Count < 2)
        {
            throw new ArgumentException("need at least two requests to get a rate", nameof(trace));
        }

        Request[] byArrival = trace.OrderBy(r => r.ArrivalMs).ToArray();
        double spanMs = byArrival[^1].ArrivalMs - byArrival[0].ArrivalMs;
        if (!(spanMs > 0))
        {
            throw new ArgumentException("all requests at the same moment, so there's no rate", nameof(trace));
        }

        double meanGapMs = spanMs / (byArrival.Length - 1);
        var random = new Random(seed);
        var result = new Request[byArrival.Length];
        double t = 0;
        for (int i = 0; i < byArrival.Length; i++)
        {
            result[i] = byArrival[i] with { ArrivalMs = t };
            t += -meanGapMs * Math.Log(1 - random.NextDouble());
        }
        return result;
    }

    // A new trace of the same length, built from blocks of the real one picked at random with
    // replacement (a block bootstrap). Bursts inside a block stay as they were; which blocks come
    // when, and how often, changes. It asks whether an answer holds beyond the one hour it was sized on.
    public static Request[] BlockBootstrap(IReadOnlyList<Request> trace, double blockMs, int seed)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.Count == 0)
        {
            throw new ArgumentException("empty trace", nameof(trace));
        }
        if (!(blockMs > 0) || double.IsInfinity(blockMs))
        {
            throw new ArgumentOutOfRangeException(nameof(blockMs), "block must be above 0");
        }

        double start = trace.Min(r => r.ArrivalMs);
        var blocks = trace.GroupBy(r => (long)Math.Floor((r.ArrivalMs - start) / blockMs))
            .ToDictionary(g => g.Key, g => g.Select(r => r with { ArrivalMs = r.ArrivalMs - start - (g.Key * blockMs) }).ToArray());
        long blockCount = (long)Math.Floor((trace.Max(r => r.ArrivalMs) - start) / blockMs) + 1;

        var random = new Random(seed);
        var result = new List<Request>();
        for (long b = 0; b < blockCount; b++)
        {
            long pick = random.NextInt64(blockCount);
            if (blocks.TryGetValue(pick, out var inBlock))
            {
                result.AddRange(inBlock.Select(r => r with { ArrivalMs = r.ArrivalMs + (b * blockMs) }));
            }
        }
        return result.ToArray();
    }

    // The most requests that arrive within any window of this length (sliding, two pointers).
    public static int BusiestWindowCount(IReadOnlyList<Request> trace, double windowMs)
    {
        ArgumentNullException.ThrowIfNull(trace);
        double[] times = trace.Select(r => r.ArrivalMs).Order().ToArray();
        int best = 0;
        for (int start = 0, end = 0; end < times.Length; end++)
        {
            while (times[end] - times[start] >= windowMs)
            {
                start++;
            }
            best = Math.Max(best, end - start + 1);
        }
        return best;
    }

    // The most machines the work in any window of this length needs just to keep up:
    // the idle prefill time of the requests that arrive in the window, divided by its length.
    // A sliding window, two pointers over arrivals. A lower bound: it ignores queueing inside the window.
    public static double PeakMachines(IReadOnlyList<Request> trace, double windowMs)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (!(windowMs > 0) || double.IsInfinity(windowMs))
        {
            throw new ArgumentOutOfRangeException(nameof(windowMs), "window must be above 0");
        }

        Request[] byArrival = trace.OrderBy(r => r.ArrivalMs).ToArray();
        double best = 0;
        double work = 0;
        for (int start = 0, end = 0; end < byArrival.Length; end++)
        {
            work += byArrival[end].PrefillMs;
            while (byArrival[end].ArrivalMs - byArrival[start].ArrivalMs >= windowMs)
            {
                work -= byArrival[start].PrefillMs;
                start++;
            }
            best = Math.Max(best, work / windowMs);
        }
        return best;
    }
}

// Variations on the check the search calls.
public static class Checks
{
    // Like MeetsSlowdownTarget, but every call multiplies prefill times by random factors, the way
    // a real load test never runs exactly the same twice. Each factor is lognormal with a mean of 1:
    // exp(sigma * z - sigma^2 / 2), z standard normal. sigma = 0 and runSigma = 0 is no noise.
    public static Func<int, CancellationToken, ValueTask<bool>> NoisySlowdownTarget(
        IReadOnlyList<Request> trace,
        IReadOnlyList<double> baselineMs,
        double percentile,
        double maxSlowdown,
        double sigma,
        Random random,
        double runSigma = 0)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!(sigma >= 0) || double.IsInfinity(sigma))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), "sigma must be 0 or more");
        }
        if (!(runSigma >= 0) || double.IsInfinity(runSigma))
        {
            throw new ArgumentOutOfRangeException(nameof(runSigma), "runSigma must be 0 or more");
        }

        // Validates the inputs the same way the plain check does.
        _ = ServingSimulator.MeetsSlowdownTarget(trace, baselineMs, percentile, maxSlowdown);
        Request[] copy = trace.ToArray();
        double[] baseline = baselineMs.ToArray();

        return (machines, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            // runSigma: one factor shared by every request in this run, the way a whole load test
            // can run a little hot or cold. sigma: each request's own factor on top.
            double run = Math.Exp((runSigma * StandardNormal(random)) - (runSigma * runSigma / 2));
            var noisy = new Request[copy.Length];
            for (int i = 0; i < copy.Length; i++)
            {
                double factor = run * Math.Exp((sigma * StandardNormal(random)) - (sigma * sigma / 2));
                noisy[i] = copy[i] with { PrefillMs = copy[i].PrefillMs * factor };
            }
            return ValueTask.FromResult(ServingSimulator.SlowdownPercentile(noisy, baseline, machines, percentile) <= maxSlowdown);
        };
    }

    // Runs the check k times (k odd) and passes when most of the runs pass.
    public static Func<int, CancellationToken, ValueTask<bool>> MajorityOf(
        int k,
        Func<int, CancellationToken, ValueTask<bool>> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (k < 1 || k % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), "k must be odd and at least 1, so there's always a majority");
        }

        return async (machines, ct) =>
        {
            int passes = 0;
            for (int i = 0; i < k; i++)
            {
                if (await check(machines, ct).ConfigureAwait(false))
                {
                    passes++;
                }
            }
            return passes > k / 2;
        };
    }

    // After a search with one run per check, re-check only the boundary: the answer and the one
    // below it, each as a majority of k. Move up while the answer fails, down while the one below passes.
    public static async Task<int> ConfirmBoundaryAsync(
        int answer,
        int k,
        Func<int, CancellationToken, ValueTask<bool>> check,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(answer, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, answer);
        var majority = MajorityOf(k, check);
        while (answer < limit && !await majority(answer, ct).ConfigureAwait(false))
        {
            answer++;
        }
        while (answer > 1 && await majority(answer - 1, ct).ConfigureAwait(false))
        {
            answer--;
        }
        return answer;
    }

    // How far into the trace a load test has to run before its result is known. A failing test is
    // decided once more requests have missed the target than the percentile allows: at the first
    // token of the miss that tips it over. A passing test has to run to the end.
    public static double MsUntilDecided(
        IReadOnlyList<Request> trace,
        IReadOnlyList<double> baselineMs,
        int machines,
        double percentile,
        double maxSlowdown)
    {
        double[] ttft = ServingSimulator.TimeToFirstToken(trace, machines);
        if (baselineMs.Count != ttft.Length)
        {
            throw new ArgumentException("need one baseline per request", nameof(baselineMs));
        }

        int allowedMisses = ttft.Length - (int)Math.Ceiling((decimal)percentile * ttft.Length / 100m);
        double end = Enumerable.Range(0, ttft.Length).Max(i => trace[i].ArrivalMs + ttft[i]);
        double[] missTimes = Enumerable.Range(0, ttft.Length)
            .Where(i => ttft[i] / baselineMs[i] > maxSlowdown)
            .Select(i => trace[i].ArrivalMs + ttft[i])
            .Order()
            .ToArray();
        return missTimes.Length > allowedMisses ? missTimes[allowedMisses] : end;
    }

    // Passes only when every window of this length, by arrival time, meets the target on its own.
    // One p99 over an hour lets a bad few minutes hide; this doesn't.
    public static Func<int, CancellationToken, ValueTask<bool>> SlowdownTargetInEveryWindow(
        IReadOnlyList<Request> trace,
        IReadOnlyList<double> baselineMs,
        double percentile,
        double maxSlowdown,
        double windowMs)
    {
        _ = ServingSimulator.MeetsSlowdownTarget(trace, baselineMs, percentile, maxSlowdown);
        if (!(windowMs > 0) || double.IsInfinity(windowMs))
        {
            throw new ArgumentOutOfRangeException(nameof(windowMs), "window must be above 0");
        }

        Request[] copy = trace.ToArray();
        double[] baseline = baselineMs.ToArray();
        return (machines, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WorstWindowSlowdown(copy, baseline, machines, percentile, windowMs) <= maxSlowdown);
        };
    }

    // The highest p-th percentile slowdown of any window.
    public static double WorstWindowSlowdown(
        IReadOnlyList<Request> trace,
        IReadOnlyList<double> baselineMs,
        int machines,
        double percentile,
        double windowMs)
    {
        double[] ttft = ServingSimulator.TimeToFirstToken(trace, machines);
        if (baselineMs.Count != ttft.Length)
        {
            throw new ArgumentException("need one baseline per request", nameof(baselineMs));
        }

        return Enumerable.Range(0, ttft.Length)
            .GroupBy(i => Math.Floor(trace[i].ArrivalMs / windowMs))
            .Max(g => ServingSimulator.Percentile(g.Select(i => ttft[i] / baselineMs[i]).ToArray(), percentile));
    }

    // Box-Muller: two uniforms in (0, 1] to one standard normal.
    private static double StandardNormal(Random random)
    {
        double u1 = 1 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
