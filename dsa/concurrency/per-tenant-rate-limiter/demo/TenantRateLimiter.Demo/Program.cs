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

return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
