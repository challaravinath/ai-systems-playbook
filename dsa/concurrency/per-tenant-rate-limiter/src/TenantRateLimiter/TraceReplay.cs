namespace TenantRateLimiter;

public sealed record Customer(string Name, IReadOnlyList<TraceRow> Rows);

public sealed record CustomerResult(string Name, int Allowed, int Limited, int TooBig, TimeSpan LongestRetryAfter, Outcome[] Outcomes)
{
    public int Requests => Outcomes.Length;
}

// Plays recorded requests through a TenantLimiter at their recorded times.
// A request that gets Limited is not retried; we only count it.
public static class TraceReplay
{
    public static double RequestsPerMinute(IReadOnlyList<TraceRow> rows) => rows.Count / Minutes(rows);

    public static double InputTokensPerMinute(IReadOnlyList<TraceRow> rows) => rows.Sum(r => (double)r.ContextTokens) / Minutes(rows);

    // Most requests inside any window of this length (a sliding window, two pointers).
    public static int BusiestWindow(IReadOnlyList<TraceRow> rows, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var times = rows.Select(r => r.Timestamp).Order().ToArray();
        int best = 0;
        for (int start = 0, end = 0; end < times.Length; end++)
        {
            while (times[end] - times[start] >= window)
            {
                start++;
            }
            best = Math.Max(best, end - start + 1);
        }
        return best;
    }

    // Our choice of limits for the demo: twice the customer's own average, for both limits.
    public static TenantLimits TwiceTheAverage(IReadOnlyList<TraceRow> rows, TimeSpan burst) =>
        new(2 * RequestsPerMinute(rows), 2 * InputTokensPerMinute(rows), burst);

    public static IReadOnlyList<CustomerResult> Run(IReadOnlyList<Customer> customers, Func<string, TenantLimits> limitsFor)
    {
        ArgumentNullException.ThrowIfNull(customers);
        if (customers.Count == 0 || customers.Any(c => c.Rows.Count == 0))
        {
            throw new ArgumentException("need at least one customer, each with requests", nameof(customers));
        }

        var limiter = new TenantLimiter(limitsFor);
        DateTime start = customers.Min(c => c.Rows.Min(r => r.Timestamp));

        // Every request from every customer, in time order. OrderBy is stable,
        // so requests at the same moment keep customer order, then row order.
        var all = customers
            .SelectMany((c, ci) => c.Rows.Select((r, ri) => (Customer: ci, Row: ri, r.Timestamp)))
            .OrderBy(x => x.Timestamp);

        var outcomes = customers.Select(c => new Outcome[c.Rows.Count]).ToArray();
        var longest = new TimeSpan[customers.Count];
        foreach (var (ci, ri, ts) in all)
        {
            var c = customers[ci];
            var d = limiter.TryAcquire(c.Name, c.Rows[ri].ContextTokens, ts - start);
            outcomes[ci][ri] = d.Outcome;
            if (d.RetryAfter > longest[ci])
            {
                longest[ci] = d.RetryAfter;
            }
        }

        return customers.Select((c, ci) => new CustomerResult(
            c.Name,
            outcomes[ci].Count(o => o == Outcome.Allowed),
            outcomes[ci].Count(o => o == Outcome.Limited),
            outcomes[ci].Count(o => o == Outcome.TooBig),
            longest[ci],
            outcomes[ci])).ToArray();
    }

    private static double Minutes(IReadOnlyList<TraceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count < 2)
        {
            throw new ArgumentException("need at least two requests to get a rate", nameof(rows));
        }

        double minutes = (rows.Max(r => r.Timestamp) - rows.Min(r => r.Timestamp)).TotalMinutes;
        if (minutes <= 0)
        {
            throw new ArgumentException("all requests at the same moment, so there's no rate", nameof(rows));
        }
        return minutes;
    }
}
