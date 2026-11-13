using System.Globalization;
using TenantRateLimiter;

// Replays the two Azure 2023 traces as two customers of one LLM API.
//   dotnet run --project demo/TenantRateLimiter.Demo -- --code <AzureLLMInferenceTrace_code.csv> --conv <AzureLLMInferenceTrace_conv.csv>

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? codePath = Arg("--code");
string? convPath = Arg("--conv");
if (codePath is null || convPath is null)
{
    Console.WriteLine("usage: --code <AzureLLMInferenceTrace_code.csv> --conv <AzureLLMInferenceTrace_conv.csv>");
    return 1;
}

Customer[] customers =
[
    new("coding", AzureTrace.Load(codePath)),
    new("conversation", AzureTrace.Load(convPath)),
];

Console.WriteLine("Two traces recorded at the same time, replayed as two customers of one API (our pairing).");
Console.WriteLine("Each customer's limits: twice its own average. A request that gets a 429 is not retried.");
Console.WriteLine();
foreach (var c in customers)
{
    var limits = TraceReplay.TwiceTheAverage(c.Rows, TimeSpan.FromMinutes(1));
    Console.WriteLine($"{c.Name}: {c.Rows.Count:N0} requests, {c.Rows.Min(r => r.Timestamp):HH:mm:ss} to {c.Rows.Max(r => r.Timestamp):HH:mm:ss}");
    Console.WriteLine($"  average      {TraceReplay.RequestsPerMinute(c.Rows),8:N1} requests/min  {TraceReplay.InputTokensPerMinute(c.Rows),10:N0} input tokens/min");
    Console.WriteLine($"  limit (x2)   {limits.RequestsPerMinute,8:N1} requests/min  {limits.InputTokensPerMinute,10:N0} input tokens/min");
    Console.WriteLine($"  busiest 60 s {TraceReplay.BusiestWindow(c.Rows, TimeSpan.FromMinutes(1)),8:N0} requests,  busiest 1 s {TraceReplay.BusiestWindow(c.Rows, TimeSpan.FromSeconds(1)):N0}");
    Console.WriteLine($"  biggest prompt {c.Rows.Max(r => r.ContextTokens):N0} tokens");
}

foreach (var burst in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10) })
{
    Console.WriteLine();
    Console.WriteLine($"Bucket holds {burst.TotalSeconds:0} seconds of the limit:");

    var limits = customers.ToDictionary(c => c.Name, c => TraceReplay.TwiceTheAverage(c.Rows, burst));
    var results = TraceReplay.Run(customers, name => limits[name]);
    foreach (var r in results)
    {
        double pct = 100.0 * r.Limited / r.Requests;
        Console.WriteLine($"  {r.Name,-13} allowed {r.Allowed,6:N0}  429 {r.Limited,6:N0} ({pct:0.00}%)  too big {r.TooBig}  longest Retry-After {r.LongestRetryAfter.TotalSeconds:0.0} s");
    }

    // Does sharing the API change anything for conversation? Replay it alone and compare.
    var alone = TraceReplay.Run([customers[1]], name => limits[name]);
    bool same = alone[0].Outcomes.SequenceEqual(results[1].Outcomes);
    Console.WriteLine($"  conversation alone gets the same answer for every request: {(same ? "yes" : "NO")}");
}

// The same traffic behind one shared bucket: both customers use one key, with both limits added.
Console.WriteLine();
Console.WriteLine("One shared bucket for both customers, with both limits added together:");
foreach (var burst in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10) })
{
    var a = TraceReplay.TwiceTheAverage(customers[0].Rows, burst);
    var b = TraceReplay.TwiceTheAverage(customers[1].Rows, burst);
    var shared = new TenantLimits(a.RequestsPerMinute + b.RequestsPerMinute, a.InputTokensPerMinute + b.InputTokensPerMinute, burst);
    var results = TraceReplay.Run(customers, _ => shared, _ => "shared");
    Console.WriteLine($"  bucket holds {burst.TotalSeconds,2:0} s: " +
        string.Join("   ", results.Select(r => $"{r.Name} 429 {r.Limited,5:N0} ({100.0 * r.Limited / r.Requests:0.00}%)")));
}

// How the result depends on our two choices: the limit (a multiple of the average) and the bucket size.
double[] multiples = [1.5, 2, 3];
double[] bursts = [1, 5, 10, 30, 60, 120];
Console.WriteLine();
Console.WriteLine("429s as a share of requests, coding / conversation, separate buckets:");
Console.WriteLine("  limit     " + string.Concat(bursts.Select(x => $"{x + " s",15}")));
var tooBig = new List<string>();
foreach (double m in multiples)
{
    var line = $"  {m,3:0.0}x avg  ";
    foreach (double sec in bursts)
    {
        var burst = TimeSpan.FromSeconds(sec);
        var results = TraceReplay.Run(customers, name => TraceReplay.TimesTheAverage(customers.First(c => c.Name == name).Rows, m, burst));
        line += $"{$"{100.0 * results[0].Limited / results[0].Requests:0.0} / {100.0 * results[1].Limited / results[1].Requests:0.0}",15}";
        foreach (var r in results.Where(r => r.TooBig > 0))
        {
            tooBig.Add($"{m:0.0}x, {sec} s: {r.Name} {r.TooBig:N0} too big");
        }
    }
    Console.WriteLine(line);
}
Console.WriteLine("  too big (prompt larger than the bucket): " + (tooBig.Count == 0 ? "none" : string.Join("; ", tooBig)));

// Clients that retry. Twice the average, 60-second bucket, up to 5 tries per request.
Console.WriteLine();
Console.WriteLine("Clients that retry (twice the average, 60 s bucket, up to 5 tries):");
var limits60 = customers.ToDictionary(c => c.Name, c => TraceReplay.TwiceTheAverage(c.Rows, TimeSpan.FromMinutes(1)));
var policies = new (string Name, RetryPolicy Policy)[]
{
    ("at Retry-After exactly", new RetryPolicy(5, false, TimeSpan.Zero)),
    ("rounded up to whole seconds, plus up to 1 s jitter", new RetryPolicy(5, true, TimeSpan.FromSeconds(1))),
};
foreach (var (pname, policy) in policies)
{
    Console.WriteLine($"  {pname}:");
    foreach (var r in TraceReplay.RunWithRetries(customers, name => limits60[name], policy, seed: 20231116))
    {
        string delays = r.AddedDelays.Length == 0 ? "" :
            $"  added delay p50 {Pct(r.AddedDelays, 0.50):0.00} s, p99 {Pct(r.AddedDelays, 0.99):0.00} s, max {r.AddedDelays[^1].TotalSeconds:0.00} s";
        Console.WriteLine($"    {r.Name,-13} first try {r.FirstTry,6:N0}  after retry {r.AfterRetry,5:N0}  gave up {r.GaveUp,4:N0}  attempts {r.Attempts,6:N0}{delays}");
    }
}

return 0;

// Nearest-rank percentile of a sorted array, in seconds.
static double Pct(TimeSpan[] sorted, double p) =>
    sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)].TotalSeconds;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
