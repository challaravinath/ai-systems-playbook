namespace FewestGpus;

// PrefillMs: how long the first token takes when the machine is free.
public readonly record struct Request(double ArrivalMs, double PrefillMs);

// A simple model of time to first token across n identical machines:
// one first-come, first-served queue, each request goes to whichever machine frees up first,
// and a machine handles one first token at a time. TTFT = wait in the queue + prefill.
// No batching, no network, no decode.
public static class ServingSimulator
{
    // TTFT per request, in the order the requests were given.
    public static double[] TimeToFirstToken(IReadOnlyList<Request> trace, int machines)
    {
        Validate(trace);
        if (machines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(machines), "need at least one machine");
        }

        // OrderBy is stable, so requests that arrive together keep their order.
        int[] order = Enumerable.Range(0, trace.Count).OrderBy(i => trace[i].ArrivalMs).ToArray();

        // Machines beyond one per request would never be used.
        int used = Math.Min(machines, trace.Count);
        var freeAt = new PriorityQueue<double, double>(used);
        for (int m = 0; m < used; m++)
        {
            freeAt.Enqueue(0, 0);
        }

        var ttft = new double[trace.Count];
        foreach (int i in order)
        {
            var req = trace[i];
            double start = Math.Max(req.ArrivalMs, freeAt.Dequeue());
            double firstToken = start + req.PrefillMs;
            ttft[i] = firstToken - req.ArrivalMs;
            freeAt.Enqueue(firstToken, firstToken);
        }
        return ttft;
    }

    // Nearest-rank percentile: sort, take the value at rank ceil(p / 100 * N). p is in (0, 100].
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("no values", nameof(values));
        }
        CheckPercentile(p);

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        // In decimal, so p / 100 * N lands exactly on a whole number when it should.
        // In double, 7 / 100.0 * 100 is 7.000000000000001, and the ceiling would skip a rank.
        int rank = (int)Math.Ceiling((decimal)p * sorted.Length / 100m);
        return sorted[Math.Max(rank, 1) - 1];
    }

    // Passes when the p-th percentile TTFT is at most targetMs.
    public static Func<int, CancellationToken, ValueTask<bool>> MeetsTarget(
        IReadOnlyList<Request> trace,
        double percentile,
        double targetMs)
    {
        Validate(trace);
        CheckPercentile(percentile);
        if (!(targetMs >= 0) || double.IsInfinity(targetMs))
        {
            throw new ArgumentOutOfRangeException(nameof(targetMs), "target must be a finite number, 0 or more");
        }

        // Copy, so the caller changing their list later can't change the answers.
        Request[] copy = trace.ToArray();
        return (machines, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Percentile(TimeToFirstToken(copy, machines), percentile) <= targetMs);
        };
    }

    // Passes when the p-th percentile of TTFT / baselineMs is at most maxSlowdown.
    // This is how Splitwise (ISCA 2024, Table VI) writes its TTFT SLO: P99 at most 6x, where the
    // baseline is the same request on an idle DGX-A100, whatever hardware is being sized.
    public static Func<int, CancellationToken, ValueTask<bool>> MeetsSlowdownTarget(
        IReadOnlyList<Request> trace,
        IReadOnlyList<double> baselineMs,
        double percentile,
        double maxSlowdown)
    {
        Validate(trace);
        ArgumentNullException.ThrowIfNull(baselineMs);
        if (baselineMs.Count != trace.Count)
        {
            throw new ArgumentException("need one baseline per request", nameof(baselineMs));
        }
        if (baselineMs.Any(b => !(b > 0) || double.IsInfinity(b)))
        {
            throw new ArgumentException("every baseline must be above 0", nameof(baselineMs));
        }
        CheckPercentile(percentile);
        if (!(maxSlowdown >= 1) || double.IsInfinity(maxSlowdown))
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlowdown), "slowdown limit must be 1 or more");
        }

        Request[] copy = trace.ToArray();
        double[] baseline = baselineMs.ToArray();
        return (machines, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SlowdownPercentile(copy, baseline, machines, percentile) <= maxSlowdown);
        };
    }

    public static double SlowdownPercentile(IReadOnlyList<Request> trace, IReadOnlyList<double> baselineMs, int machines, double percentile)
    {
        ArgumentNullException.ThrowIfNull(baselineMs);
        double[] ttft = TimeToFirstToken(trace, machines);
        if (baselineMs.Count != ttft.Length)
        {
            throw new ArgumentException("need one baseline per request", nameof(baselineMs));
        }

        var slowdown = new double[ttft.Length];
        for (int i = 0; i < ttft.Length; i++)
        {
            slowdown[i] = ttft[i] / baselineMs[i];
        }
        return Percentile(slowdown, percentile);
    }

    // Doubling from 1, capped at one machine per request (past that nothing changes).
    public static Task<SearchResult> FewestReplicasAsync(
        IReadOnlyList<Request> trace,
        Func<int, CancellationToken, ValueTask<bool>> check,
        CancellationToken ct = default)
    {
        Validate(trace);
        return AnswerSearch.FewestThatPassUnboundedAsync(1, trace.Count, check, ct);
    }

    private static void CheckPercentile(double p)
    {
        if (!(p > 0 && p <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(p), "percentile must be in (0, 100]");
        }
    }

    private static void Validate(IReadOnlyList<Request> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.Count == 0)
        {
            throw new ArgumentException("empty trace", nameof(trace));
        }
        for (int i = 0; i < trace.Count; i++)
        {
            var r = trace[i];
            if (!double.IsFinite(r.ArrivalMs) || r.ArrivalMs < 0 || !double.IsFinite(r.PrefillMs) || r.PrefillMs < 0)
            {
                throw new ArgumentException($"request {i} has a bad arrival or prefill time", nameof(trace));
            }
        }
    }
}
