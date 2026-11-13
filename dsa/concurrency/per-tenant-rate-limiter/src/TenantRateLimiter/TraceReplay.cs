namespace TenantRateLimiter;

public sealed record Customer(string Name, IReadOnlyList<TraceRow> Rows);

// How a client retries after a 429. Retry-After is a fraction of a second in this code;
// HTTP sends whole seconds, so a client may round it up. Jitter adds a random wait of up to MaxJitter.
public sealed record RetryPolicy(int MaxAttempts, bool RoundUpToWholeSeconds, TimeSpan MaxJitter);

// AddedDelays: for each request that passed on a retry, how much later than its first try it passed. Sorted.
public sealed record RetryResult(
    string Name, int Requests, int FirstTry, int AfterRetry, int GaveUp, int TooBig, int Attempts, TimeSpan[] AddedDelays);

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

    // Our choice of limits for the demo: a multiple of the customer's own average, for both limits.
    public static TenantLimits TimesTheAverage(IReadOnlyList<TraceRow> rows, double multiple, TimeSpan burst) =>
        new(multiple * RequestsPerMinute(rows), multiple * InputTokensPerMinute(rows), burst);

    public static TenantLimits TwiceTheAverage(IReadOnlyList<TraceRow> rows, TimeSpan burst) =>
        TimesTheAverage(rows, 2, burst);

    // tenantOf maps a customer's name to the limiter key. By default each customer is its own key.
    // Give two customers the same key and they share one pair of buckets.
    public static IReadOnlyList<CustomerResult> Run(
        IReadOnlyList<Customer> customers, Func<string, TenantLimits> limitsFor, Func<string, string>? tenantOf = null)
    {
        tenantOf ??= name => name;
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
            var d = limiter.TryAcquire(tenantOf(c.Name), c.Rows[ri].ContextTokens, ts - start);
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

    // Plays the trace again, but a request that gets a 429 comes back after its Retry-After,
    // up to MaxAttempts tries in all. Retries and new requests go through one priority queue
    // ordered by time, so a retry competes with the requests that arrive at the same moment.
    public static IReadOnlyList<RetryResult> RunWithRetries(
        IReadOnlyList<Customer> customers, Func<string, TenantLimits> limitsFor, RetryPolicy policy, int seed)
    {
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(policy);
        if (customers.Count == 0 || customers.Any(c => c.Rows.Count == 0))
        {
            throw new ArgumentException("need at least one customer, each with requests", nameof(customers));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxAttempts, 1);

        var limiter = new TenantLimiter(limitsFor);
        var random = new Random(seed);
        DateTime start = customers.Min(c => c.Rows.Min(r => r.Timestamp));

        // Ties on time go by a sequence number: first the trace order, then retries in the order they were made.
        var queue = new PriorityQueue<(int Customer, int Row, int Attempt), (TimeSpan Time, long Seq)>();
        long seq = 0;
        var firstTries = customers
            .SelectMany((c, ci) => c.Rows.Select((r, ri) => (Customer: ci, Row: ri, Time: r.Timestamp - start)))
            .OrderBy(x => x.Time);
        foreach (var (ci, ri, time) in firstTries)
        {
            queue.Enqueue((ci, ri, 1), (time, seq++));
        }

        var stats = customers.Select(_ => new RetryStats()).ToArray();
        while (queue.TryDequeue(out var item, out var at))
        {
            var c = customers[item.Customer];
            var row = c.Rows[item.Row];
            var s = stats[item.Customer];
            s.Attempts++;

            var d = limiter.TryAcquire(c.Name, row.ContextTokens, at.Time);
            if (d.Outcome == Outcome.Allowed)
            {
                if (item.Attempt == 1)
                {
                    s.FirstTry++;
                }
                else
                {
                    s.AfterRetry++;
                    s.AddedDelays.Add(at.Time - (row.Timestamp - start));
                }
            }
            else if (d.Outcome == Outcome.TooBig)
            {
                s.TooBig++;
            }
            else if (item.Attempt == policy.MaxAttempts)
            {
                s.GaveUp++;
            }
            else
            {
                TimeSpan wait = policy.RoundUpToWholeSeconds ? TimeSpan.FromSeconds(Math.Ceiling(d.RetryAfter.TotalSeconds)) : d.RetryAfter;
                wait += policy.MaxJitter * random.NextDouble();
                queue.Enqueue((item.Customer, item.Row, item.Attempt + 1), (at.Time + wait, seq++));
            }
        }

        return customers.Select((c, ci) => new RetryResult(
            c.Name, c.Rows.Count, stats[ci].FirstTry, stats[ci].AfterRetry, stats[ci].GaveUp, stats[ci].TooBig,
            stats[ci].Attempts, stats[ci].AddedDelays.Order().ToArray())).ToArray();
    }

    private sealed class RetryStats
    {
        public int FirstTry;
        public int AfterRetry;
        public int GaveUp;
        public int TooBig;
        public int Attempts;
        public List<TimeSpan> AddedDelays { get; } = [];
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
