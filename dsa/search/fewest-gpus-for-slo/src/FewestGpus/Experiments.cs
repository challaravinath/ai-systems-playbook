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
    // Like MeetsSlowdownTarget, but every call multiplies each prefill time by its own random
    // factor, the way a real load test never runs exactly the same twice. The factor is lognormal
    // with a mean of 1: exp(sigma * z - sigma^2 / 2), z standard normal. sigma = 0 is no noise.
    public static Func<int, CancellationToken, ValueTask<bool>> NoisySlowdownTarget(
        IReadOnlyList<Request> trace,
        IReadOnlyList<double> baselineMs,
        double percentile,
        double maxSlowdown,
        double sigma,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!(sigma >= 0) || double.IsInfinity(sigma))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), "sigma must be 0 or more");
        }

        // Validates the inputs the same way the plain check does.
        _ = ServingSimulator.MeetsSlowdownTarget(trace, baselineMs, percentile, maxSlowdown);
        Request[] copy = trace.ToArray();
        double[] baseline = baselineMs.ToArray();

        return (machines, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var noisy = new Request[copy.Length];
            for (int i = 0; i < copy.Length; i++)
            {
                double factor = Math.Exp((sigma * StandardNormal(random)) - (sigma * sigma / 2));
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
