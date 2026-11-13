namespace FewestGpus;

public readonly record struct Check(int Candidate, bool Passed);

public sealed record SearchResult(int? Answer, IReadOnlyList<Check> Log)
{
    public int Checks => Log.Count;
}

// Binary search on the answer. The check has to be monotone: once it passes for n,
// it passes for every bigger n. Checks are async because in real life each one is a load test.
public static class AnswerSearch
{
    // Smallest n in [lo, hi] that passes, or null.
    public static async Task<SearchResult> FewestThatPassAsync(
        int lo,
        int hi,
        Func<int, CancellationToken, ValueTask<bool>> passes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passes);
        CheckRange(lo, hi);

        var log = new List<Check>();
        int? best = await SearchAsync(lo, hi, passes, log, ct).ConfigureAwait(false);
        return new SearchResult(best, log);
    }

    // For when there's no good upper bound: try start, 2*start, 4*start ... until one passes,
    // then binary search between the last fail and that pass. Never goes above limit.
    public static async Task<SearchResult> FewestThatPassUnboundedAsync(
        int start,
        int limit,
        Func<int, CancellationToken, ValueTask<bool>> passes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passes);
        if (start < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "start must be at least 1, or doubling never moves");
        }
        CheckRange(start, limit);

        var log = new List<Check>();
        int lastFail = start - 1;
        int probe = start;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool ok = await passes(probe, ct).ConfigureAwait(false);
            log.Add(new Check(probe, ok));
            if (ok)
            {
                break;
            }

            lastFail = probe;
            if (probe == limit)
            {
                return new SearchResult(null, log);
            }
            probe = probe > limit / 2 ? limit : probe * 2;
        }

        // probe passed, so the answer is somewhere in (lastFail, probe].
        int? inside = await SearchAsync(lastFail + 1, probe - 1, passes, log, ct).ConfigureAwait(false);
        return new SearchResult(inside ?? probe, log);
    }

    // Everything below lo is known to fail. best is null, or it is hi + 1 and it passed,
    // so it is the smallest pass seen so far.
    private static async ValueTask<int?> SearchAsync(
        int lo,
        int hi,
        Func<int, CancellationToken, ValueTask<bool>> passes,
        List<Check> log,
        CancellationToken ct)
    {
        int? best = null;
        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();

            int mid = lo + ((hi - lo) / 2);
            bool ok = await passes(mid, ct).ConfigureAwait(false);
            log.Add(new Check(mid, ok));

            if (ok)
            {
                best = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return best;
    }

    private static void CheckRange(int lo, int hi)
    {
        if (lo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lo), "lo can't be negative");
        }
        if (hi < lo)
        {
            throw new ArgumentOutOfRangeException(nameof(hi), "hi can't be below lo");
        }
        if (hi == int.MaxValue)
        {
            // mid + 1 would overflow
            throw new ArgumentOutOfRangeException(nameof(hi), "hi must be below int.MaxValue");
        }
    }
}
